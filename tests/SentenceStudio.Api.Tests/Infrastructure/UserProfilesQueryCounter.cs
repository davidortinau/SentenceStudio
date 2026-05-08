using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// EF Core command interceptor that counts SELECT statements hitting the
/// UserProfiles table. Used to assert that ProfileEndpoints does NOT use the
/// fetch-all-then-filter anti-pattern documented in
/// .squad/decisions.md (2026-05-08 entry).
///
/// Detects fetch-all by inspecting whether the SELECT against UserProfiles
/// includes a WHERE clause referencing the Id column. A scan with no WHERE
/// (or with only an OFFSET/LIMIT but no Id predicate) counts as a fetch-all.
/// </summary>
public sealed class UserProfilesQueryCounter : DbCommandInterceptor
{
    private readonly object _gate = new();
    private int _userProfilesSelects;
    private int _userProfilesFetchAll;
    private readonly List<string> _captured = new();

    public int UserProfilesSelectCount
    {
        get { lock (_gate) return _userProfilesSelects; }
    }

    public int UserProfilesFetchAllCount
    {
        get { lock (_gate) return _userProfilesFetchAll; }
    }

    public IReadOnlyList<string> CapturedQueries
    {
        get { lock (_gate) return _captured.ToArray(); }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _userProfilesSelects = 0;
            _userProfilesFetchAll = 0;
            _captured.Clear();
        }
    }

    private void Inspect(DbCommand command)
    {
        var text = command.CommandText ?? string.Empty;
        if (text.Length == 0) return;

        var touchesUserProfiles =
            text.Contains("\"UserProfile\"", StringComparison.OrdinalIgnoreCase) ||
            text.Contains(" UserProfile ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("FROM \"UserProfile\"", StringComparison.OrdinalIgnoreCase);

        if (!touchesUserProfiles) return;
        if (!text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;

        lock (_gate)
        {
            _userProfilesSelects++;
            _captured.Add(text);

            // Heuristic: a query is a fetch-all if there is no WHERE clause at all,
            // or there is a WHERE clause that does NOT reference the Id column. EF
            // Core's translation of FirstOrDefaultAsync(p => p.Id == ...) always
            // includes "Id" in the predicate.
            var upper = text.ToUpperInvariant();
            var hasWhere = System.Text.RegularExpressions.Regex.IsMatch(upper, @"\sWHERE\s");
            var whereTouchesId = hasWhere && upper.Contains("\"ID\"");
            if (!hasWhere || !whereTouchesId)
            {
                _userProfilesFetchAll++;
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Inspect(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
