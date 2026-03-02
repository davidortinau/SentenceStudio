
using CoreSync;
using CoreSync.Http.Server;
using SentenceStudio.Data;
using SentenceStudio.Web;

var dbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sentencestudio", "server");
Directory.CreateDirectory(dbFolder);

string databasePath = Path.Combine(dbFolder, "sentencestudio.db");

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure HTTPS for development
// builder.WebHost.ConfigureKestrel(options =>
// {
//     options.ListenLocalhost(5241, listenOptions =>
//     {
//         listenOptions.UseHttps();
//     });
// });

builder.Services.AddDataServices(databasePath);
builder.Services.AddSyncServices(databasePath);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();


var app = builder.Build();

// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment())
// {
//     app.MapOpenApi();
// }

app.UseCoreSyncHttpServer();

app.UseLatestDatabaseVersion();

app.SetupServerSynchronization();

// Endpoint for clients to query server max PKs before sync (prevents PK collisions)
app.MapGet("/api/sync/table-maxids", (IServiceProvider sp) =>
{
    var syncProvider = sp.GetRequiredService<ISyncProvider>();
    // Use the same DB path as the sync provider
    var connectionString = $"Data Source={databasePath}";
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
    connection.Open();
    
    var tables = new[] { "UserProfile", "SkillProfile", "VocabularyWord", "VocabularyList",
        "LearningResource", "ResourceVocabularyMapping", "Challenge", "Conversation",
        "ConversationChunk", "VocabularyProgress", "VocabularyLearningContext" };
    
    var result = new Dictionary<string, long>();
    foreach (var table in tables)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(Id), 0) FROM [{table}]";
        var maxId = (long)(cmd.ExecuteScalar() ?? 0L);
        result[table] = maxId;
    }
    
    return Results.Ok(result);
});

// app.UseHttpsRedirection();

app.Run();