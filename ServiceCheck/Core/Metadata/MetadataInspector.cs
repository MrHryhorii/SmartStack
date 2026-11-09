using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;

namespace Tsubaki.ServiceCheck.Core.Metadata;

/// <summary>Collects metadata using the shared run deadline supplied by its caller.</summary>
public static class MetadataInspector
{
    /// <summary>Requests metadata sequentially and preserves completed observations.</summary>
    public static async Task<(
        MetadataInspectionResult Inspection,
        DiagnosticExecutionState ExecutionState)> InspectAsync(
        TsubakiClient client,
        DiagnosticMode mode,
        string? requestedVoice,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (mode is not (DiagnosticMode.Check or DiagnosticMode.Inspect))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (mode == DiagnosticMode.Check)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedVoice);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runCancellationToken);

        var observations = new List<(
            MetadataObservation Observation,
            long DurationMs)>(4);

        var continueInspection = await ObserveAsync(
            client,
            MetadataEndpoint.Health,
            observations,
            cancellationToken,
            runCancellationToken,
            linked.Token);

        if (continueInspection &&
            observations[^1].Observation.Http.StatusCode is 404 or 405)
        {
            continueInspection = await ObserveAsync(
                client,
                MetadataEndpoint.HealthFallback,
                observations,
                cancellationToken,
                runCancellationToken,
                linked.Token);
        }

        if (continueInspection)
        {
            continueInspection = await ObserveAsync(
                client,
                MetadataEndpoint.Status,
                observations,
                cancellationToken,
                runCancellationToken,
                linked.Token);
        }

        if (continueInspection)
        {
            await ObserveAsync(
                client,
                MetadataEndpoint.Voices,
                observations,
                cancellationToken,
                runCancellationToken,
                linked.Token);
        }

        var executionState = GetExecutionState(
            observations,
            cancellationToken,
            runCancellationToken);

        var inspection = MetadataEvidenceBuilder.Build(
            mode,
            observations,
            requestedVoice,
            client.Redact,
            executionState);

        return (inspection, executionState);
    }

    /// <summary>Captures one endpoint and reports whether collection can continue.</summary>
    private static async Task<bool> ObserveAsync(
        TsubakiClient client,
        MetadataEndpoint endpoint,
        List<(MetadataObservation Observation, long DurationMs)> observations,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken,
        CancellationToken parsingCancellationToken)
    {
        if (parsingCancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var response = await client.RequestMetadataAsync(
            endpoint,
            cancellationToken,
            runCancellationToken);

        var observation = ParseResponse(
            response,
            client,
            cancellationToken,
            parsingCancellationToken);

        observations.Add((observation, response.TotalDurationMs));

        return !parsingCancellationToken.IsCancellationRequested &&
            observation.Code is not ("cancelled" or "deadline_exceeded");
    }

    /// <summary>Parses available bytes while retaining transport evidence on interruption.</summary>
    private static MetadataObservation ParseResponse(
        MetadataRequestResult response,
        TsubakiClient client,
        CancellationToken cancellationToken,
        CancellationToken parsingCancellationToken)
    {
        if (response.State != MetadataRequestState.Completed)
        {
            return MetadataParser.Parse(response);
        }

        try
        {
            return MetadataParser.Parse(
                response,
                parsingCancellationToken);
        }
        catch (OperationCanceledException) when (
            parsingCancellationToken.IsCancellationRequested)
        {
            var callerCancelled = cancellationToken.IsCancellationRequested;

            return MetadataParser.Parse(response with
            {
                State = callerCancelled
                    ? MetadataRequestState.Cancelled
                    : MetadataRequestState.TimedOut,
                Code = callerCancelled ? "cancelled" : "deadline_exceeded",
                Summary = callerCancelled
                    ? "Metadata parsing was cancelled."
                    : "Metadata parsing was interrupted by the overall run deadline.",
                Payload = null
            });
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException)
        {
            return MetadataParser.Parse(response with
            {
                State = MetadataRequestState.Error,
                Code = "client_error",
                Summary = "The utility could not parse this metadata response.",
                ExceptionType = client.Redact(exception.GetType().Name),
                Payload = null
            });
        }
    }

    /// <summary>Distinguishes whole-run interruption from local metadata failures.</summary>
    private static DiagnosticExecutionState GetExecutionState(
        IReadOnlyList<(MetadataObservation Observation, long DurationMs)> observations,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return DiagnosticExecutionState.Cancelled;
        }

        if (runCancellationToken.IsCancellationRequested)
        {
            return DiagnosticExecutionState.TimedOut;
        }

        var lastCode = observations.Count == 0
            ? null
            : observations[^1].Observation.Code;

        return lastCode switch
        {
            "cancelled" => DiagnosticExecutionState.Cancelled,
            "deadline_exceeded" => DiagnosticExecutionState.TimedOut,
            _ => DiagnosticExecutionState.Completed
        };
    }
}
