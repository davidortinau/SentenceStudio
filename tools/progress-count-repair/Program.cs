using System.Globalization;
using System.Text;
using SentenceStudio.Tools.ProgressCountRepair;

// progress-count-repair --owner <id> --progress <progress.json> --contexts <contexts.json> --out <dir>
// Writes <dir>/summary.txt, <dir>/changes.tsv and <dir>/repair.sql (PostgreSQL). It reads only the
// exports and never connects to a database. Exit 1 when a planned row would lower a count.
var options = args.Chunk(2).Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
if (!options.TryGetValue("--owner", out var owner) || !options.TryGetValue("--progress", out var progressPath)
    || !options.TryGetValue("--contexts", out var contextsPath) || !options.TryGetValue("--out", out var outDir))
{
    Console.Error.WriteLine("usage: progress-count-repair --owner <id> --progress <json> --contexts <json> --out <dir>");
    return 64;
}

var (progress, rawUpdatedAt) = ExportReader.ReadProgress(File.ReadAllText(progressPath));
var contexts = ExportReader.ReadContexts(File.ReadAllText(contextsPath));
var changes = RepairPlanner.Plan(owner, progress, contexts, DateTime.UtcNow);

var lowered = changes.Where(change => change.After.TotalAttempts < change.Before.TotalAttempts
    || change.After.CorrectAttempts < change.Before.CorrectAttempts
    || change.After.ExposureCount < change.Before.ExposureCount
    || change.After.LastPracticedAt < change.Before.LastPracticedAt).ToList();
var masteryDown = changes.Where(change => change.After.MasteryScore < change.Before.MasteryScore).ToList();
var others = progress.Count(row => !string.Equals(row.UserId, owner, StringComparison.Ordinal));

Directory.CreateDirectory(outDir);
var summary = new StringBuilder()
    .AppendLine(CultureInfo.InvariantCulture, $"owner {owner}")
    .AppendLine(CultureInfo.InvariantCulture, $"progress rows read {progress.Count} (other owners, ignored: {others}); contexts read {contexts.Count}")
    .AppendLine(CultureInfo.InvariantCulture, $"rows to repair {changes.Count}")
    .AppendLine(CultureInfo.InvariantCulture, $"attempts {changes.Sum(c => c.Before.TotalAttempts)} -> {changes.Sum(c => c.After.TotalAttempts)}")
    .AppendLine(CultureInfo.InvariantCulture, $"correct {changes.Sum(c => c.Before.CorrectAttempts)} -> {changes.Sum(c => c.After.CorrectAttempts)}")
    .AppendLine(CultureInfo.InvariantCulture, $"words at 0 attempts {changes.Count(c => c.Before.TotalAttempts == 0)} -> {changes.Count(c => c.After.TotalAttempts == 0)}")
    .AppendLine(CultureInfo.InvariantCulture, $"status Known {changes.Count(c => c.Before.IsKnown)} -> {changes.Count(c => c.After.IsKnown)}; Learning {changes.Count(c => c.Before.IsLearning)} -> {changes.Count(c => c.After.IsLearning)}; Unknown {changes.Count(c => c.Before.IsUnknown)} -> {changes.Count(c => c.After.IsUnknown)}")
    .AppendLine(CultureInfo.InvariantCulture, $"mastery lower after replay {masteryDown.Count} (largest drop {(masteryDown.Count == 0 ? 0 : masteryDown.Max(c => c.Before.MasteryScore - c.After.MasteryScore)):0.000})")
    .AppendLine(CultureInfo.InvariantCulture, $"rows that would lower a count or date {lowered.Count}");
File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());

var table = new StringBuilder("ProgressId\tWordId\tActiveAnswers\tAttempts\tCorrect\tMastery\tNextReview\tChangedColumns\n");
foreach (var change in changes)
    table.AppendLine(string.Join('\t', change.Before.Id, change.Before.VocabularyWordId,
        change.ActiveAnswers.ToString(CultureInfo.InvariantCulture),
        $"{change.Before.TotalAttempts}->{change.After.TotalAttempts}",
        $"{change.Before.CorrectAttempts}->{change.After.CorrectAttempts}",
        string.Create(CultureInfo.InvariantCulture, $"{change.Before.MasteryScore:0.000}->{change.After.MasteryScore:0.000}"),
        $"{change.Before.NextReviewDate:yyyy-MM-dd}->{change.After.NextReviewDate:yyyy-MM-dd}",
        string.Join(',', RepairPlanner.ChangedColumns(change.Before, change.After))));
File.WriteAllText(Path.Combine(outDir, "changes.tsv"), table.ToString());
// Only a safe plan gets SQL: a plan that would lower a count leaves none to run by mistake.
var sqlPath = Path.Combine(outDir, "repair.sql");
if (lowered.Count == 0)
    File.WriteAllText(sqlPath, SqlWriter.Write(owner, changes, rawUpdatedAt));
else if (File.Exists(sqlPath))
    File.Delete(sqlPath);

Console.Write(summary);
return lowered.Count == 0 ? 0 : 1;
