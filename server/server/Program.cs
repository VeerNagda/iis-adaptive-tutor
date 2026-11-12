using server.Data;
using server.LLM;
using server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()));

var connectionString = builder.Configuration.GetConnectionString("Sqlite")
    ?? "Data Source=adaptive_tutor.db";

builder.Services.AddSingleton<IDbConnectionFactory>(
    new SqliteConnectionFactory(connectionString));

// Register whichever LLM provider is selected in config (default: Mock).
// Set LlmProvider=Gemini in appsettings.Development.json to use the real API.
var llmProvider = builder.Configuration["LlmProvider"] ?? "Mock";
if (llmProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<GeminiProvider>();
    builder.Services.AddSingleton<ILlmProvider, GeminiProvider>();

    builder.Services.AddHttpClient<GeminiEmbedder>();
    builder.Services.AddSingleton<IGeminiEmbedder, GeminiEmbedder>();
}
else
{
    builder.Services.AddSingleton<ILlmProvider, MockLlmProvider>();
    builder.Services.AddSingleton<IGeminiEmbedder, MockGeminiEmbedder>();
}

// Ingestion pipeline: processes uploaded documents in the background.
builder.Services.AddScoped<IngestionService>();
// TEMPORARILY DISABLED: auto-ingest polls every 30s (and retries 'failed' docs), burning Gemini credits.
// Re-enable by uncommenting the next line.
// builder.Services.AddHostedService<IngestionBackgroundService>();

var app = builder.Build();

// Initialize DB on startup
var schemaPath = Path.Combine(AppContext.BaseDirectory, "database", "schema.sql");
var dbFactory = app.Services.GetRequiredService<IDbConnectionFactory>();
DatabaseInitializer.Initialize(dbFactory, schemaPath);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
