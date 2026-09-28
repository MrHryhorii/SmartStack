using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace STT_Runner.Endpoints;

// Describe multipart uploads without restricting the endpoint's raw audio content types.
public sealed class TranscriptionUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath is not
            ("v1/audio/transcriptions" or "v1/audio/translations")) return;

        var schema = context.SchemaGenerator.GenerateSchema(
            typeof(TranscriptionEndpoints.UploadForm), context.SchemaRepository);
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new() { Schema = schema }
            }
        };
    }
}
