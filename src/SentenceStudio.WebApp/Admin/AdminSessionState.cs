using SentenceStudio.WebUI.Services;

namespace SentenceStudio.WebApp.Admin;

/// <summary>How a password confirmation ended. The zero value is a failure, never a confirmation.</summary>
public enum AdminPasswordOutcome
{
    Failed = 0,
    Confirmed = 1,
    Incorrect = 2,
    LockedOut = 3,
    NotAdmin = 4
}

/// <summary>
/// What one browser tab knows about its admin: the access answer the sidebar asked for, and the
/// password-again proof while it lasts.
/// </summary>
/// <remarks>
/// Scoped, so it lives and dies with one circuit. The proof never leaves the server: it is held
/// here and sent to the API from here, and a reload simply asks for the password again.
/// </remarks>
public sealed class AdminSessionState(AdminApiClient api, TimeProvider time)
{
    /// <summary>How long the sidebar's count is shown before it is asked for again.</summary>
    public static readonly TimeSpan AttentionCountMaxAge = TimeSpan.FromMinutes(2);

    private Task<AdminAccess>? _sidebarProbe;
    private Task<int?>? _attentionCount;
    private DateTimeOffset _attentionCountAskedAt;
    private string? _proof;
    private DateTimeOffset? _proofExpiresAtUtc;

    /// <summary>Raised when the password confirmation starts or stops being live.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether to draw the sidebar link. The API's yes or no is kept for the life of the circuit
    /// (the desktop and mobile menus share it); a probe that could not get an answer is not
    /// kept, so the next ask tries again. The link is a convenience: every admin page and every
    /// admin request asks the API for itself.
    /// </summary>
    public async Task<bool> ProbeForSidebarAsync()
    {
        var probe = _sidebarProbe ??= api.ProbeAsync();
        var access = await probe;
        if (access == AdminAccess.Unknown && ReferenceEquals(_sidebarProbe, probe))
        {
            _sidebarProbe = null;
        }

        return access == AdminAccess.Admin;
    }

    /// <summary>
    /// How many flagged answers nobody has looked at yet, for the sidebar's count, or null when
    /// the API did not say. The sidebar asks on every navigation, so an answer is kept for
    /// <see cref="AttentionCountMaxAge"/> and then asked for again: a tab left open still learns
    /// of a learner's new flag on the next navigation after that.
    /// </summary>
    public async Task<int?> GetReportAttentionCountAsync()
    {
        if (_attentionCount is null || time.GetUtcNow() - _attentionCountAskedAt >= AttentionCountMaxAge)
        {
            _attentionCountAskedAt = time.GetUtcNow();
            _attentionCount = AskReportAttentionCountAsync();
        }

        var ask = _attentionCount;
        var count = await ask;
        if (count is null && ReferenceEquals(_attentionCount, ask))
        {
            // Not knowing is not remembered: the next ask tries again.
            _attentionCount = null;
        }

        return count;
    }

    private async Task<int?> AskReportAttentionCountAsync()
    {
        var result = await api.GetReportAttentionCountAsync();
        return result.IsOk ? result.Value!.Sam : null;
    }

    /// <summary>The inbox has just read the count for itself; the sidebar shows the same number.</summary>
    public void SetReportAttentionCount(int count)
    {
        _attentionCountAskedAt = time.GetUtcNow();
        _attentionCount = Task.FromResult<int?>(count);
    }

    /// <summary>When the confirmed password stops counting, or null when it does not count now.</summary>
    public DateTimeOffset? PasswordConfirmedUntilUtc =>
        _proofExpiresAtUtc is { } expires && expires > time.GetUtcNow() ? expires : null;

    /// <summary>The proof to send with a dangerous request, or null when none is live.</summary>
    public string? CurrentProof => PasswordConfirmedUntilUtc is null ? null : _proof;

    /// <summary>Checks the password with the API and keeps the proof it issues.</summary>
    public async Task<AdminPasswordOutcome> ConfirmPasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        var result = await api.StepUpAsync(password, cancellationToken);
        switch (result.Status)
        {
            case AdminApiStatus.Ok when result.Value is { } issued:
                _proof = issued.Proof;
                _proofExpiresAtUtc = issued.ExpiresAtUtc;
                Changed?.Invoke();
                return AdminPasswordOutcome.Confirmed;

            case AdminApiStatus.NotFound:
                MarkNotAdmin();
                return AdminPasswordOutcome.NotAdmin;

            case AdminApiStatus.Refused when result.Code == "password_incorrect":
                return AdminPasswordOutcome.Incorrect;

            case AdminApiStatus.Refused when result.Code == "locked_out":
                return AdminPasswordOutcome.LockedOut;

            default:
                return AdminPasswordOutcome.Failed;
        }
    }

    /// <summary>
    /// Brings this tab's idea of the confirmation in line with the API's, which is the one that
    /// counts. <paramref name="apiExpiry"/> is what the API reported for the proof just sent:
    /// null means it no longer honours it (the password changed, for instance).
    /// </summary>
    public void Reconcile(DateTimeOffset? apiExpiry)
    {
        if (_proof is null)
        {
            return;
        }

        if (apiExpiry is null)
        {
            ForgetProof();
        }
        else if (apiExpiry != _proofExpiresAtUtc)
        {
            _proofExpiresAtUtc = apiExpiry;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The API has said this person is not an admin (any more). Drops the proof and stops
    /// drawing the sidebar link.
    /// </summary>
    public void MarkNotAdmin()
    {
        _sidebarProbe = Task.FromResult(AdminAccess.NotAdmin);
        _attentionCount = null;
        ForgetProof();
    }

    private void ForgetProof()
    {
        var hadProof = _proof is not null;
        _proof = null;
        _proofExpiresAtUtc = null;
        if (hadProof)
        {
            Changed?.Invoke();
        }
    }
}

/// <summary>Adds the Admin link to the shared sidebar, for an admin only.</summary>
/// <remarks>
/// The link carries the number of flagged answers nobody has looked at yet, so an admin notices
/// them from any page. No number is shown when there are none or the API did not say.
/// </remarks>
public sealed class AdminNavItemProvider(AdminSessionState state) : IHostNavItemProvider
{
    private static readonly IReadOnlyList<HostNavItem> AdminItems =
        [new HostNavItem("/admin", "bi-shield-lock", "Admin")];

    public async ValueTask<IReadOnlyList<HostNavItem>> GetItemsAsync(CancellationToken cancellationToken = default)
    {
        if (!await state.ProbeForSidebarAsync())
        {
            return [];
        }

        return await state.GetReportAttentionCountAsync() is > 0 and var count
            ? [new HostNavItem("/admin", "bi-shield-lock", "Admin", Badge: count, BadgeLabel: AttentionLabel(count))]
            : AdminItems;
    }

    internal static string AttentionLabel(int count) =>
        count == 1 ? "1 flagged answer to look at" : $"{count} flagged answers to look at";
}
