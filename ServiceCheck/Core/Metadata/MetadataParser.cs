using System.Text;
using System.Text.Json;
using Tsubaki.ServiceCheck.Core.Diagnostics;
using Tsubaki.ServiceCheck.Core.Http;

namespace Tsubaki.ServiceCheck.Core.Metadata;

/// <summary>Parses bounded metadata into selected, typed server observations.</summary>
public static class MetadataParser
{
    public const int MaxVoiceIds = 512;
    public const int MaxFieldCharacters = 256;

    private const int MaxDocumentBytes = 4 * 1024 * 1024;
    private const int MaxProperties = 4096;
    private const int MaxWarnings = 32;
    private const string ExpectedService = "Tsubaki TTS Engine";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Parses one completed response or preserves its unavailable coverage.</summary>
    public static MetadataObservation Parse(
        MetadataRequestResult response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();

        var observation = CreateObservation(response);

        if (response.State != MetadataRequestState.Completed ||
            response.StatusCode != 200)
        {
            return observation;
        }

        var payload = response.Payload;

        if (!response.BodyComplete || payload is null)
        {
            return observation with
            {
                Code = "metadata_body_unavailable",
                Summary = "A complete metadata body was not available for parsing."
            };
        }

        if (payload.Length > MaxDocumentBytes)
        {
            return observation with
            {
                State = MetadataDocumentState.Invalid,
                Code = "response_limit_exceeded",
                Summary = "Metadata exceeded the parser's document size limit."
            };
        }

        try
        {
            StrictUtf8.GetCharCount(payload);

            ReadOnlyMemory<byte> json = payload;

            if (payload.Length >= 3 &&
                payload[0] == 0xEF &&
                payload[1] == 0xBB &&
                payload[2] == 0xBF)
            {
                json = json[3..];
            }

            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });

            cancellationToken.ThrowIfCancellationRequested();

            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return observation with
                {
                    State = MetadataDocumentState.Unrecognized,
                    Code = "unexpected_content",
                    Summary = "Metadata response is not a JSON object."
                };
            }

            var propertyCount = 0;

            var structureError = ValidateStructure(
                root,
                ref propertyCount,
                cancellationToken);

            if (structureError is not null)
            {
                return observation with
                {
                    State = MetadataDocumentState.Invalid,
                    Code = structureError,
                    Summary = structureError == "metadata_duplicate_property"
                        ? "Metadata JSON contains duplicate property names."
                        : "Metadata JSON exceeded its property count limit."
                };
            }

            var reader = new MetadataReader(cancellationToken);
            reader.CheckMediaType(response.MediaType);

            return response.Endpoint switch
            {
                MetadataEndpoint.Health or MetadataEndpoint.HealthFallback =>
                    ParseService(root, observation, reader, includeStatus: false),

                MetadataEndpoint.Status =>
                    ParseService(root, observation, reader, includeStatus: true),

                MetadataEndpoint.Voices =>
                    ParseVoices(root, observation, reader),

                _ => observation with
                {
                    State = MetadataDocumentState.Unrecognized,
                    Code = "metadata_endpoint_unsupported",
                    Summary = "The metadata endpoint is not supported by this parser."
                }
            };
        }
        catch (Exception exception) when (
            exception is JsonException or
                DecoderFallbackException or
                InvalidOperationException)
        {
            return observation with
            {
                State = MetadataDocumentState.Invalid,
                Code = "metadata_invalid_json",
                Summary = "Metadata response does not contain valid JSON."
            };
        }
    }

    /// <summary>Copies selected transport evidence without retaining response bytes.</summary>
    private static MetadataObservation CreateObservation(
        MetadataRequestResult response)
    {
        return new MetadataObservation
        {
            Endpoint = response.Endpoint,
            State = MetadataDocumentState.Unavailable,
            Code = response.Code,
            Summary = response.Summary,
            ObservedAtUtc = response.FinishedAtUtc.ToUniversalTime(),
            Http = new DiagnosticHttpEvidence
            {
                Method = "GET",
                Route = response.Route,
                StatusCode = response.StatusCode,
                MediaType = response.MediaType,
                DeclaredContentLength = response.DeclaredContentLength,
                BodyBytesRead = response.BodyBytesRead,
                BodyComplete = response.BodyComplete,
                RedirectLocation = response.RedirectLocation,
                ErrorExcerpt = response.ErrorExcerpt,
                ErrorExcerptTruncated = response.ErrorExcerptTruncated,
                ExceptionType = response.ExceptionType
            }
        };
    }

    /// <summary>Recognizes health or status only when its service label matches.</summary>
    private static MetadataObservation ParseService(
        JsonElement root,
        MetadataObservation observation,
        MetadataReader reader,
        bool includeStatus)
    {
        var service = reader.ReadString(root, "service", "service");

        var identityMatched = service is not null &&
            string.Equals(
                service.Trim(),
                ExpectedService,
                StringComparison.OrdinalIgnoreCase);

        if (!identityMatched)
        {
            return observation with
            {
                State = MetadataDocumentState.Unrecognized,
                Code = "unexpected_content",
                Summary = "The response did not identify the expected Tsubaki service.",
                Service = service,
                Warnings = reader.Warnings.ToArray()
            };
        }

        var version = reader.ReadString(root, "version", "version");
        var status = reader.ReadString(root, "status", "status");
        var readiness = reader.ReadBoolean(
            root,
            "synthesisReady",
            "synthesisReady");

        var timestamp = reader.ReadTimestamp(root, "timestamp", "timestamp");

        var voices = includeStatus
            ? reader.ReadVoices(root, "availableVoices", "availableVoices")
            : null;

        var snapshot = includeStatus
            ? ReadStatus(root, reader)
            : null;

        return observation with
        {
            State = MetadataDocumentState.Recognized,
            Code = "metadata_recognized",
            Summary = includeStatus
                ? "Recognized Tsubaki status metadata."
                : "Recognized Tsubaki health metadata.",
            ServiceIdentityMatched = true,
            Service = service,
            Version = version,
            Status = status,
            SynthesisReady = readiness,
            ServerTimestampUtc = timestamp,
            Voices = voices,
            StatusSnapshot = snapshot,
            Warnings = reader.Warnings.ToArray()
        };
    }

    /// <summary>Recognizes advertised voices without inferring service identity.</summary>
    private static MetadataObservation ParseVoices(
        JsonElement root,
        MetadataObservation observation,
        MetadataReader reader)
    {
        var voices = reader.ReadVoices(root, "voices", "voices");

        return observation with
        {
            State = voices is null
                ? MetadataDocumentState.Unrecognized
                : MetadataDocumentState.Recognized,
            Code = voices is null
                ? "unexpected_content"
                : "metadata_recognized",
            Summary = voices is null
                ? "The response did not contain an advertised voice list."
                : "Recognized advertised voice metadata.",
            Voices = voices,
            Warnings = reader.Warnings.ToArray()
        };
    }

    /// <summary>Reads selected capabilities and defaults from recognized status.</summary>
    private static MetadataStatusSnapshot ReadStatus(
        JsonElement root,
        MetadataReader reader)
    {
        var model = reader.ReadObject(root, "model", "model");
        var cloning = reader.ReadObject(root, "voiceCloning", "voiceCloning");
        var formats = reader.ReadObject(root, "formats", "formats");
        var dsp = reader.ReadObject(root, "dsp", "dsp");
        var limits = reader.ReadObject(root, "limits", "limits");
        var rate = reader.ReadObject(limits, "rateLimit", "limits.rateLimit");

        var textLimitKnown = false;
        int? maxTextLength = null;

        if (TryGet(limits, "maxTextLength", out var textLimit))
        {
            if (textLimit.ValueKind == JsonValueKind.Null)
            {
                textLimitKnown = true;
            }
            else
            {
                maxTextLength = reader.ReadInteger(
                    limits,
                    "maxTextLength",
                    "limits.maxTextLength");

                textLimitKnown = maxTextLength.HasValue;
            }
        }

        var rateLimit = rate.ValueKind == JsonValueKind.Object
            ? new MetadataRateLimitSnapshot(
                reader.ReadInteger(rate, "permitLimit", "limits.rateLimit.permitLimit"),
                reader.ReadNumber(rate, "windowSeconds", "limits.rateLimit.windowSeconds", minimum: 0),
                reader.ReadInteger(rate, "queueLimit", "limits.rateLimit.queueLimit"))
            : null;

        return new MetadataStatusSnapshot
        {
            BaseVoiceDialect = reader.ReadString(model, "baseVoiceDialect", "model.baseVoiceDialect"),
            SampleRateHz = reader.ReadInteger(model, "sampleRateHz", "model.sampleRateHz", minimum: 1),
            VoiceCloningEnabled = reader.ReadBoolean(cloning, "enabled", "voiceCloning.enabled"),
            VoiceCloningAvailable = reader.ReadBoolean(cloning, "available", "voiceCloning.available"),
            WavAvailable = reader.ReadBoolean(formats, "wav", "formats.wav"),
            DefaultSampleRate = reader.ReadInteger(dsp, "defaultSampleRate", "dsp.defaultSampleRate"),
            DefaultPitch = reader.ReadNumber(dsp, "defaultPitch", "dsp.defaultPitch"),
            DefaultVolume = reader.ReadNumber(dsp, "defaultVolume", "dsp.defaultVolume"),
            VolumeBoosterDb = reader.ReadNumber(dsp, "volumeBoosterDb", "dsp.volumeBoosterDb"),
            DefaultTotalGainDb = reader.ReadNumber(dsp, "defaultTotalGainDb", "dsp.defaultTotalGainDb"),
            TextLimitKnown = textLimitKnown,
            MaxTextLength = maxTextLength,
            RateLimit = rateLimit
        };
    }

    /// <summary>Rejects ambiguous object names and excessive property counts.</summary>
    private static string? ValidateStructure(
        JsonElement element,
        ref int propertyCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in element.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++propertyCount > MaxProperties)
                {
                    return "metadata_structure_limit";
                }

                if (!names.Add(property.Name))
                {
                    return "metadata_duplicate_property";
                }

                var error = ValidateStructure(
                    property.Value,
                    ref propertyCount,
                    cancellationToken);

                if (error is not null)
                {
                    return error;
                }
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in element.EnumerateArray())
        {
            var error = ValidateStructure(
                item,
                ref propertyCount,
                cancellationToken);

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>Reads a property only from an available object.</summary>
    private static bool TryGet(
        JsonElement parent,
        string name,
        out JsonElement value)
    {
        value = default;

        return parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out value);
    }

    /// <summary>Reads guarded fields and records bounded, value-free warnings.</summary>
    private sealed class MetadataReader
    {
        private readonly CancellationToken _cancellationToken;
        private readonly List<MetadataWarning> _warnings = [];

        public IReadOnlyList<MetadataWarning> Warnings => _warnings;

        /// <summary>Stores the cancellation token used while reading voice lists.</summary>
        public MetadataReader(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        /// <summary>Warns when JSON bytes arrived with a missing or different media type.</summary>
        public void CheckMediaType(string? mediaType)
        {
            if (mediaType is not null &&
                (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                 mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Warn(
                "metadata_media_type",
                "The metadata media type was missing or differed from JSON.");
        }

        /// <summary>Reads an optional object without interpreting other JSON types.</summary>
        public JsonElement ReadObject(JsonElement parent, string name, string path)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return default;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }

            WarnField(path);
            return default;
        }

        /// <summary>Reads bounded nonblank text without control characters.</summary>
        public string? ReadString(JsonElement parent, string name, string path)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();

                if (text is not null && IsValidText(text))
                {
                    return text;
                }
            }

            WarnField(path);
            return null;
        }

        /// <summary>Reads only a JSON boolean and preserves an unknown value as null.</summary>
        public bool? ReadBoolean(JsonElement parent, string name, string path)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            WarnField(path);
            return null;
        }

        /// <summary>Reads a representable JSON integer at or above its allowed minimum.</summary>
        public int? ReadInteger(
            JsonElement parent,
            string name,
            string path,
            int minimum = 0)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number) &&
                number >= minimum)
            {
                return number;
            }

            WarnField(path);
            return null;
        }

        /// <summary>Reads a finite JSON number without coercing strings.</summary>
        public double? ReadNumber(
            JsonElement parent,
            string name,
            string path,
            double? minimum = null)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) &&
                double.IsFinite(number) &&
                (!minimum.HasValue || number >= minimum.Value))
            {
                return number;
            }

            WarnField(path);
            return null;
        }

        /// <summary>Reads an ISO timestamp with an explicit UTC offset as a server claim.</summary>
        public DateTimeOffset? ReadTimestamp(
            JsonElement parent,
            string name,
            string path)
        {
            var text = ReadString(parent, name, path);

            if (text is null)
            {
                return null;
            }

            var separator = text.IndexOf('T');

            var hasOffset = text.EndsWith('Z') ||
                (separator >= 0 &&
                 (text.LastIndexOf('+') > separator ||
                  text.LastIndexOf('-') > separator));

            if (hasOffset &&
                parent.GetProperty(name).TryGetDateTimeOffset(out var timestamp))
            {
                return timestamp.ToUniversalTime();
            }

            WarnField(path);
            return null;
        }

        /// <summary>Retains bounded distinct voice IDs and marks incomplete coverage.</summary>
        public MetadataVoiceList? ReadVoices(
            JsonElement parent,
            string name,
            string path)
        {
            if (!TryGet(parent, name, out var value) ||
                value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                WarnField(path);
                return null;
            }

            var ids = new List<string>();
            var retained = new HashSet<string>(StringComparer.Ordinal);
            var complete = true;

            foreach (var item in value.EnumerateArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var text = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : null;

                if (text is null || !IsValidText(text))
                {
                    complete = false;
                    WarnField(path);
                    continue;
                }

                if (retained.Contains(text))
                {
                    continue;
                }

                if (ids.Count >= MaxVoiceIds)
                {
                    complete = false;

                    Warn(
                        "metadata_voice_limit",
                        "The advertised voice list exceeded the retained voice limit.");

                    continue;
                }

                retained.Add(text);
                ids.Add(text);
            }

            return new MetadataVoiceList(ids.ToArray(), complete);
        }

        /// <summary>Checks the common bounds applied to selected text and voice IDs.</summary>
        private static bool IsValidText(string text)
        {
            return text.Length <= MaxFieldCharacters &&
                !string.IsNullOrWhiteSpace(text) &&
                !text.Any(char.IsControl);
        }

        /// <summary>Records a known field path without copying its rejected value.</summary>
        private void WarnField(string path)
        {
            Warn(
                "metadata_field_invalid",
                $"Metadata field '{path}' was ignored because it had an unsupported value.");
        }

        /// <summary>Deduplicates warnings and bounds their retained count.</summary>
        private void Warn(string code, string summary)
        {
            if (_warnings.Count >= MaxWarnings ||
                _warnings.Any(warning =>
                    warning.Code == code && warning.Summary == summary))
            {
                return;
            }

            _warnings.Add(new MetadataWarning(code, summary));
        }
    }
}
