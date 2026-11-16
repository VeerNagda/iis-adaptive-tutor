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
    // HttpClient gives up after 100 seconds by default. A call that sends several
    // images (ingestion) can take longer than that, so the limit is raised.
    builder.Services.AddHttpClient<GeminiProvider>(client => client.Timeout = TimeSpan.FromMinutes(5));
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
// TEMPORARILY DISABLED: auto-ingest polls every 30s (and retries 'failed' docs), burning Gemini credits.
// Re-enable by uncommenting the next line.
// builder.Services.AddHostedService<IngestionBackgroundService>();

var app = builder.Build();

if (!useGemini)
    app.Logger.LogWarning("*** MOCK MODE *** No Gemini API key found (or LlmProvider=Mock): "
        + "lessons and quizzes are canned mock content, not generated from an LLM.");

// Initialize DB on startup
var schemaPath = Path.Combine(AppContext.BaseDirectory, "database", "schema.sql");
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
