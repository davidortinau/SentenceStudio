using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Data;

/// <summary>
/// Learning-progress timestamps are UTC instants on every store.
/// <para>
/// SQLite keeps <c>DateTime</c> as zone-less text and CoreSync sends that text to a PostgreSQL
/// <c>timestamptz</c> column, which reads it as UTC; the API's legacy Npgsql setting hands
/// <c>timestamptz</c> back as <see cref="DateTimeKind.Local"/>. Writing a local wall clock on a
/// device therefore stored "local labeled UTC" (5 h off in US Central, 9 h in Korea) while the
/// server-side merge, plan completion and activity sessions use UTC.
/// </para>
/// <para>
/// This converter stores the instant (a <see cref="DateTimeKind.Local"/> value is converted;
/// an <see cref="DateTimeKind.Unspecified"/> value is taken as UTC) and reads every value back
/// as <see cref="DateTimeKind.Utc"/>. Column types and the SQLite text format are unchanged, so
/// no migration is needed and SQL comparisons with already-stored text keep working. Rows written
/// before this change keep their historic offset; see docs/native-auth-readiness.md.
/// </para>
/// Calendar-day columns (<c>DailyPlan.Date</c>, <c>DailyPlanCompletion.Date</c>,
/// <c>DiaryEntry.EntryDate</c>) are user-local days, not instants, and are deliberately not listed.
/// </summary>
internal static class UtcInstantModelConfiguration
{
    internal static readonly ValueConverter<DateTime, DateTime> UtcInstantConverter = new(
        value => ToUtcInstant(value),
        value => ToUtcInstant(value));

    /// <summary>Entities whose every <c>DateTime</c> column is an instant.</summary>
    internal static readonly Type[] InstantEntities =
    [
        typeof(VocabularyProgress),
        typeof(VocabularyLearningContext),
        typeof(UserActivity),
        typeof(WordAssociationScore),
        typeof(NumberAttempt),
        typeof(NumberMasteryProgress),
    ];

    internal static DateTime ToUtcInstant(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    internal static void ConfigureUtcInstants(this ModelBuilder modelBuilder)
    {
        foreach (var clrType in InstantEntities)
        {
            if (modelBuilder.Model.FindEntityType(clrType) is not { } entity)
                continue;
            foreach (var property in entity.GetProperties()
                         .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
            {
                property.SetValueConverter(UtcInstantConverter);
            }
        }
    }
}
