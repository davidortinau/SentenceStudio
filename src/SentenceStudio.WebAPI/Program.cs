using CommunityToolkit.Datasync.Server;
using SentenceStudio.WebAPI.Data;
using SentenceStudio.WebAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<AppDbContext>(connectionName: "SentenceStudioDB");
builder.Services.AddScoped<IVocabularyListRepository, VocabularyListRepository>();
builder.Services.AddDatasyncServices();
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
