namespace SentenceStudio.WebUI.Shared.Sam;

/// <summary>
/// Lets an entry outside the overlay host, the <c>PageHeader</c> icon, open Sam's panel the same
/// way the floating button does (docs/sam-entry.md).
/// </summary>
/// <remarks>
/// <para>
/// The overlay host owns everything about opening: the account check, resuming the learner's last
/// conversation, the panel size, inertness and focus. It attaches here and says whether an entry
/// may show, which is exactly when it would draw its own floating button: a signed-in learner, past
/// onboarding and the first sync, Sam enabled for them, and the panel closed. Nothing attaches when
/// the layout's coach gate keeps the host out, so the header icon follows the same gate.
/// </para>
/// <para>
/// Scoped like the coach workspace: one per circuit on the web, one per web view on MAUI. On MAUI
/// the layout and host are rebuilt inside a scope that lives on, so a host only changes this state
/// while it is the attached one; a host being torn down after its replacement attached cannot
/// switch the entry off.
/// </para>
/// </remarks>
public sealed class SamEntryState
{
    private object? _owner;
    private Func<string, Task>? _open;

    /// <summary>True when an entry may show and opening it will open the panel.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Sam answered while the panel was closed; the entry shows the unread mark.</summary>
    public bool HasUnread { get; private set; }

    /// <summary>Raised when <see cref="IsAvailable"/> or <see cref="HasUnread"/> changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// The overlay host takes over the entry. <paramref name="open"/> receives the id of the
    /// control that asked, so focus can return to it when the panel closes.
    /// </summary>
    public void Attach(object owner, Func<string, Task> open)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(open);

        _owner = owner;
        _open = open;
    }

    /// <summary>Reports whether the entry may show. Ignored from a host that is not attached.</summary>
    public void Publish(object owner, bool isAvailable, bool hasUnread)
    {
        if (!ReferenceEquals(owner, _owner))
        {
            return;
        }

        hasUnread &= isAvailable;
        if (IsAvailable == isAvailable && HasUnread == hasUnread)
        {
            return;
        }

        IsAvailable = isAvailable;
        HasUnread = hasUnread;
        Changed?.Invoke();
    }

    /// <summary>The host is going away: no entry until another host attaches.</summary>
    public void Detach(object owner)
    {
        if (!ReferenceEquals(owner, _owner))
        {
            return;
        }

        Publish(owner, isAvailable: false, hasUnread: false);
        _owner = null;
        _open = null;
    }

    /// <summary>
    /// Opens the panel through the host's own open path. Does nothing when no entry may show,
    /// so a click queued before the panel opened or the learner signed out opens nothing.
    /// </summary>
    /// <param name="invokerElementId">The id of the control that asked.</param>
    public Task OpenAsync(string invokerElementId) =>
        IsAvailable && _open is { } open ? open(invokerElementId) : Task.CompletedTask;
}
