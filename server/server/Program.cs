using server.Data;
using server.LLM;
using server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        // The frontend's address. Docker compose can override it with Cors__Origin.
        policy.WithOrigins(builder.Configuration["Cors:Origin"] ?? "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()));

var connectionString = builder.Configuration.GetConnectionString("Sqlite")
    ?? "Data Source=adaptive_tutor.db";

builder.Services.AddSingleton<IDbConnectionFactory>(
    new SqliteConnectionFactory(connectionString));

// Choose the LLM provider:
//   - Gemini when an API key is available (env var GEMINI_API_KEY, see LlmKey),
//   - otherwise the mock, which returns canned lessons and quizzes so the whole
//     flow can be demoed without a key.
// Setting LlmProvider=Mock forces the mock even when a key exists (no quota used).
var forceMock = "Mock".Equals(builder.Configuration["LlmProvider"], StringComparison.OrdinalIgnoreCase);
var useGemini = !forceMock && LlmKey.Read(builder.Configuration) is not null;
if (useGemini)
{
    // HttpClient gives up after 100 seconds by default. A call that sends images
    // (ingestion) has been seen to take almost 5 minutes, so the limit is 10 minutes.
    builder.Services.AddHttpClient<GeminiProvider>(client => client.Timeout = TimeSpan.FromMinutes(10));
    // Resolved through the typed client above, so that this timeout is the one used.
    builder.Services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<GeminiProvider>());

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
// Adaptive teaching: topics, lessons, quizzes, checkpoints, history.
builder.Services.AddScoped<LearningService>();
// Auto-ingest is OFF by default: it polls every 30s (and retries 'failed' docs),
// which can use up Gemini credits quickly. Turn it on with AutoIngest=true
// (env var AUTO_INGEST=true in docker compose). Otherwise files are ingested by
// hand with the "Ingest" button.
var autoIngest = builder.Configuration.GetValue<bool>("AutoIngest");
if (autoIngest)
    builder.Services.AddHostedService<IngestionBackgroundService>();

var app = builder.Build();

if (!useGemini)
    app.Logger.LogWarning("*** MOCK MODE *** No Gemini API key found (or LlmProvider=Mock): "
        + "lessons and quizzes are canned mock content, not generated from an LLM.");

if (autoIngest)
    app.Logger.LogWarning("Auto-ingest is ON: uploaded files are ingested automatically and use LLM quota.");

// Initialize DB on startup
var schemaPath = Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql");
var dbFactory = app.Services.GetRequiredService<IDbConnectionFactory>();
DatabaseInitializer.Initialize(dbFactory, schemaPath);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
// The container only serves plain http, so redirecting to https would break it.
if (!app.Environment.IsProduction())
    app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
