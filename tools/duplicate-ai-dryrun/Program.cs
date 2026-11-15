using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.DTOs;

// duplicate-ai-dryrun --db <copy of sentencestudio.sqlite> --owner <id> --out <dir>
//   [--endpoint https://<account>.openai.azure.com] [--native English] [--target Korean]
// Opens the copy read-only, asks the AI about every duplicate group to review (fast tier, then the
// reasoning tier for the unsure ones, as the app will), and writes <dir>/report.md and
// <dir>/calls.json. Keyless: DefaultAzureCredential (az login). It changes no data.
var options = args.Chunk(2).Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
if (!options.TryGetValue("--db", out var dbPath) || !options.TryGetValue("--owner", out var owner)
    || !options.TryGetValue("--out", out var outDir))
{
    Console.Error.WriteLine("usage: duplicate-ai-dryrun --db <sqlite> --owner <id> --out <dir> [--endpoint <url>] [--native <lang>] [--target <lang>]");
    return 64;
}
if (!File.Exists(dbPath))
{
    Console.Error.WriteLine($"No database at {dbPath}");
    return 66;
}
var endpoint = options.GetValueOrDefault("--endpoint") ?? "https://daortin-sstudio-eus2.openai.azure.com";
var native = options.GetValueOrDefault("--native") ?? "English";
var target = options.GetValueOrDefault("--target") ?? "Korean";

var services = new ServiceCollection();
services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
services.AddDbContext<ApplicationDbContext>(db => db.UseSqlite($"Data Source={dbPath};Mode=ReadOnly"));
services.AddSingleton<IFileSystemService, NoFiles>();
services.AddScoped<LearningResourceRepository>();
using var provider = services.BuildServiceProvider();

IReadOnlyList<VocabularyDuplicateAiGroup> groups;
using (var scope = provider.CreateScope())
    groups = await scope.ServiceProvider.GetRequiredService<LearningResourceRepository>()
        .GetDuplicateGroupsForAiAsync(userProfileId: owner);
Console.WriteLine($"{groups.Count} duplicate groups to review");
if (groups.Count == 0)
    return 0;

var azure = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
var clients = new Dictionary<AiTier, IChatClient>
{
    [AiTier.Fast] = azure.GetChatClient("gpt-5-mini").AsIChatClient(),
    [AiTier.Reasoning] = azure.GetChatClient("gpt-5").AsIChatClient(),
};
var asked = new List<(AiTier Tier, int Groups, TimeSpan Took, long? InputTokens, long? OutputTokens)>();
async Task<DuplicateResolutionResponse?> Ask(string prompt, AiTier tier, CancellationToken ct)
{
    var started = DateTime.UtcNow;
    try
    {
        var response = await clients[tier].GetResponseAsync<DuplicateResolutionResponse>(
            [new ChatMessage(ChatRole.User, prompt)], AiChatOptionsFactory.Create(), cancellationToken: ct);
        asked.Add((tier, prompt.Split("\"term\":").Length - 1, DateTime.UtcNow - started,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount));
        return response.Result;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{tier} call failed: {ex.GetType().Name}: {ex.Message}");
        throw;
    }
}

var results = await VocabularyDuplicateResolver.ResolveAsync(groups, native, target, Ask, null, CancellationToken.None);

Directory.CreateDirectory(outDir);
var byId = groups.ToDictionary(group => group.GroupId, StringComparer.Ordinal);
var report = new StringBuilder();
var sure = results.Where(result => result.Call.IsSure).ToList();
report.AppendLine(CultureInfo.InvariantCulture, $"# AI duplicate dry run ({DateTime.Now:yyyy-MM-dd HH:mm})")
    .AppendLine()
    .AppendLine(CultureInfo.InvariantCulture, $"{groups.Count} groups to review. Would apply without asking: {sure.Count} "
        + $"({sure.Count(result => result.Call.Verdict == VocabularyDuplicateAiVerdict.SameWord)} merge, "
        + $"{sure.Count(result => result.Call.Verdict == VocabularyDuplicateAiVerdict.DifferentWords)} keep apart, "
        + $"{sure.Count(result => result.Call.Verdict == VocabularyDuplicateAiVerdict.Mixed)} mixed). "
        + $"Needs you: {results.Count - sure.Count}. Asked again on the reasoning tier: {results.Count(result => result.FastCall is not null)}.")
    .AppendLine()
    .AppendLine(CultureInfo.InvariantCulture, $"Calls: {string.Join("; ", asked.Select(call => $"{call.Tier} {call.Groups} groups {call.Took.TotalSeconds:0}s in {call.InputTokens} out {call.OutputTokens} tokens"))}")
    .AppendLine()
    .AppendLine("| Term | Copies | AI call | Kept meaning(s) | Conf. | Tier | Applies? | Reason |")
    .AppendLine("|---|---|---|---|---|---|---|---|");
foreach (var result in results
             .OrderByDescending(result => result.Call.IsSure)
             .ThenBy(result => result.Call.Verdict)
             .ThenBy(result => byId[result.Call.GroupId].Term, StringComparer.Ordinal))
{
    var group = byId[result.Call.GroupId];
    var copies = group.Copies.ToDictionary(copy => copy.Id, StringComparer.Ordinal);
    var call = result.Call;
    report.AppendLine(CultureInfo.InvariantCulture, $"| {Cell(group.Term)} "
        + $"| {Cell(string.Join("<br>", group.Copies.Select(copy => $"\"{copy.Meaning}\"{(string.IsNullOrWhiteSpace(copy.Language) || copy.Language == "Korean" ? "" : $" [{copy.Language}]")}")))} "
        + $"| {(call.Usable ? call.Verdict.ToString() : $"unusable: {call.Problem}")} "
        + $"| {Cell(string.Join("<br>", call.Sets.Select(set => $"{string.Join(" + ", set.CopyIds.Select(id => $"\"{copies[id].Meaning}\""))} -> **{set.Meaning}**{(set.Language is null ? "" : $" [{set.Language}]")}")))} "
        + $"| {call.Confidence:0.00} | {result.DecidedBy} | {(call.IsSure ? "yes" : "no")} | {Cell(call.Reason)} |");
}
File.WriteAllText(Path.Combine(outDir, "report.md"), report.ToString());
File.WriteAllText(Path.Combine(outDir, "calls.json"), JsonSerializer.Serialize(results.Select(result => new
{
    group = byId[result.Call.GroupId],
    result.DecidedBy,
    result.Call,
    result.FastCall,
}), new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
}));
Console.WriteLine($"Would apply {sure.Count}, needs you {results.Count - sure.Count}. Report: {Path.Combine(outDir, "report.md")}");
return 0;

static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

/// <summary>The dry run reads no app files.</summary>
internal sealed class NoFiles : IFileSystemService
{
    public string AppDataDirectory => Path.GetTempPath();

    public Task<Stream> OpenAppPackageFileAsync(string filename) =>
        throw new FileNotFoundException("The dry run reads no app package files.", filename);
}
