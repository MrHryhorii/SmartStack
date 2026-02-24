using ONNX_Runner;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Define paths for the model assets
// AppContext.BaseDirectory points to the folder where the .exe is running
string modelsPath = Path.Combine(AppContext.BaseDirectory, "Models", "Chatterbox");
string tokenizerPath = Path.Combine(modelsPath, "tokenizer.json");

// Register AI services as Singletons
// This ensures models are loaded into VRAM only once at startup
builder.Services.AddSingleton(new TtsModelManager(modelsPath));
builder.Services.AddSingleton(new TextProcessor(tokenizerPath));

WebApplication app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Basic health check endpoint
app.MapGet("/", () => "ONNX Runner is working! Go to /swagger to test the API.");

// OpenAI-compatible endpoint for speech generation
// Currently used to verify the Tokenizer output
app.MapPost("/v1/audio/speech", (string text, TtsModelManager modelManager, TextProcessor textProcessor) =>
{
    try
    {
        // Convert input text into token IDs
        int[] tokens = textProcessor.Tokenize(text);

        // Return JSON response for verification purposes
        return Results.Ok(new
        {
            Message = "Text tokenized successfully!",
            OriginalText = text,
            TokenIds = tokens
        });
    }
    catch (Exception ex)
    {
        // Return a standard error response if tokenization fails
        return Results.Problem(detail: ex.Message, title: "Processing Error");
    }
});

app.Run();