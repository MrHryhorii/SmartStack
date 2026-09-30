using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace STT_Runner.Endpoints;

// OpenAPI metadata is documentation only: Accepts<T> would reject raw audio
// before the handler can inspect its Content-Type.
public sealed class TranscriptionUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath is not
            ("v1/audio/transcriptions" or "v1/audio/translations")) return;

        Type form = context.ApiDescription.RelativePath == "v1/audio/translations"
            ? typeof(TranscriptionEndpoints.TranslationUploadForm)
            : typeof(TranscriptionEndpoints.TranscriptionUploadForm);
        var schema = context.SchemaGenerator.GenerateSchema(form, context.SchemaRepository);
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
