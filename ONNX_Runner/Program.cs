using ONNX_Runner;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Swagger is for testing API directly from the browser
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// TODO: Here we will register AI services (Model Manager, Tokenizer, etc.)

// Get a folder with models
string modelsPath = Path.Combine(AppContext.BaseDirectory, "Models", "Chatterbox");

// Add TtsModelManager as Singleton
builder.Services.AddSingleton(new TtsModelManager(modelsPath));

WebApplication app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// API Endpoints
app.MapGet("/", () => "ONNX Runner is working! Go to /swagger to test the API.");

// TODO: Here we will add the OpenAI compatible endpoint: app.MapPost("/v1/audio/speech", ...)

app.Run();