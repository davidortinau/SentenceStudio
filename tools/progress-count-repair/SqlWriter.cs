using System.Globalization;
using System.Text;

namespace SentenceStudio.Tools.ProgressCountRepair;

/// <summary>
/// PostgreSQL UPDATE statements for a plan. Each changes only the columns the fold changed and
/// applies only while the row is still the one read: the same owner, UpdatedAt and TotalAttempts.
/// A row practiced since the export updates nothing (UPDATE 0) and keeps its newer answer.
/// </summary>
public static class SqlWriter
{
    public static string Write(string owner, IReadOnlyList<RepairChange> changes,
        IReadOnlyDictionary<string, string> rawUpdatedAt)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"-- Progress count repair for owner {owner}: {changes.Count} rows.");
        foreach (var change in changes)
        {
            var columns = RepairPlanner.ChangedColumns(change.Before, change.After);
            var set = string.Join(", ", columns.Select(column =>
                $"\"{column}\" = {Literal(RepairPlanner.Value(change.After, column))}"));
            sql.AppendLine(
                $"UPDATE \"VocabularyProgress\" SET {set} WHERE \"Id\" = {Literal(change.Before.Id)}"
                + $" AND \"UserId\" = {Literal(owner)}"
                + $" AND \"UpdatedAt\" = {Stamp(change, rawUpdatedAt)}::timestamptz"
                + $" AND \"TotalAttempts\" = {Literal(change.Before.TotalAttempts)};");
        }
        return sql.ToString();
    }

    /// <summary>
    /// The exported UpdatedAt as PostgreSQL wrote it (exact, with its offset); a timestamp without
    /// an offset (a SQLite export, stored as UTC) is written as UTC, never read in the session zone.
    /// </summary>
    private static string Stamp(RepairChange change, IReadOnlyDictionary<string, string> rawUpdatedAt) =>
        rawUpdatedAt.TryGetValue(change.Before.Id, out var raw)
            && System.Text.RegularExpressions.Regex.IsMatch(raw, @"(Z|[+-]\d{2}(:?\d{2})?)$")
            ? Literal(raw)
            : Literal(change.Before.UpdatedAt);

    public static string Literal(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        float number => float.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : throw new ArgumentException($"Non-finite value {number}."),
        DateTime stamp when stamp == DateTime.MinValue => "'-infinity'",
        DateTime stamp when stamp == DateTime.MaxValue => "'infinity'",
        DateTime stamp => "'" + DateTime.SpecifyKind(stamp, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture) + "'",
        Enum flag => Convert.ToInt32(flag, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"No SQL literal for {value.GetType().Name}."),
    };
}
