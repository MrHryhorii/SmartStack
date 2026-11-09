using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Tsubaki.ServiceCheck.Regression;

/// <summary>Supplies restricted metadata responses around an existing speech fixture.</summary>
internal sealed class FixtureMetadataHandler : DelegatingHandler
{
    private static readonly byte[] ErrorBody = Encoding.UTF8.GetBytes(
        """{"error":"Metadata is restricted by this fixture."}""");

    /// <summary>Stores the handler that receives non-GET requests.</summary>
    public FixtureMetadataHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>Returns controlled metadata failures and forwards speech requests.</summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Method != HttpMethod.Get)
        {
            return base.SendAsync(request, cancellationToken);
        }

        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new ByteArrayContent(ErrorBody)
        };

        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        return Task.FromResult(response);
    }
}
