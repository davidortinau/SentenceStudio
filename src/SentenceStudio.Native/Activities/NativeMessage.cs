using System.Collections.Immutable;
using System.Globalization;

namespace SentenceStudio.Native;

/// <summary>Source ToastService kinds (ToastService.cs: ShowSuccess/ShowInfo/ShowWarning/ShowError).</summary>
public enum NativeMessageKind { Success, Info, Warning, Error }

/// <summary>
/// How a message is shown (Captain, 2026-11-02, "split by kind"): errors and
/// warnings that need action are an inline banner that stays until the
/// learner's next action; success and info are a transient toast that
/// disappears on its own, like the source toasts.
/// </summary>
public enum NativeMessagePresentation { Banner, Toast }

public static class NativeMessageAccess
{
    public const string Toast = "native.message.toast";
    public const string ToastDismiss = "native.message.toast.dismiss";
    public const string Banner = "native.message.banner";
}

/// <summary>
/// One activity message. <see cref="Key"/> is a localization key formatted
/// with <see cref="Argument"/> when present; when Key is null,
/// <see cref="Argument"/> is literal text (for example a translation result).
/// Each instance has its own <see cref="Id"/>, so the same text shown twice is
/// two toasts, while a state copied with <c>with</c> keeps the same message.
/// </summary>
public sealed record NativeMessage(NativeMessageKind Kind, string? Key, string? Argument = null)
{
    private static long s_nextId;

    public long Id { get; init; } = Interlocked.Increment(ref s_nextId);

    public NativeMessagePresentation Presentation =>
        Kind is NativeMessageKind.Success or NativeMessageKind.Info
            ? NativeMessagePresentation.Toast : NativeMessagePresentation.Banner;

    public bool IsToast => Presentation == NativeMessagePresentation.Toast;

    public bool IsBanner => Presentation == NativeMessagePresentation.Banner;

    public bool IsError => Kind == NativeMessageKind.Error;

    /// <summary>Equal content (kind, key, argument); <see cref="Id"/> tells two showings apart.</summary>
    public bool Equals(NativeMessage? other) =>
        other is not null && Kind == other.Kind
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && string.Equals(Argument, other.Argument, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Kind, Key, Argument);

    public static NativeMessage Success(string key, string? argument = null) =>
        new(NativeMessageKind.Success, key, argument);

    public static NativeMessage Info(string key, string? argument = null) =>
        new(NativeMessageKind.Info, key, argument);

    public static NativeMessage Warning(string key, string? argument = null) =>
        new(NativeMessageKind.Warning, key, argument);

    public static NativeMessage Error(string key, string? argument = null) =>
        new(NativeMessageKind.Error, key, argument);

    /// <summary>The localized text: the key's resource, formatted with the argument when both are present.</summary>
    public string Format(Func<string, string> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Key is null)
            return Argument ?? string.Empty;
        var template = text(Key);
        return Argument is null ? template
            : string.Format(CultureInfo.CurrentCulture, template, Argument);
    }
}

public static class NativeMessages
{
    /// <summary>Messages shown as the inline banner (errors and warnings), in order.</summary>
    public static ImmutableArray<NativeMessage> Banners(ImmutableArray<NativeMessage> messages) =>
        messages.IsDefaultOrEmpty ? [] : [.. messages.Where(message => message.IsBanner)];

    public static ImmutableArray<NativeMessage> Banners(NativeMessage? message) =>
        message is { IsBanner: true } ? [message] : [];

    /// <summary>The newest toast (success or info) among the messages, if any.</summary>
    public static NativeMessage? LatestToast(ImmutableArray<NativeMessage> messages) =>
        messages.IsDefaultOrEmpty ? null : messages.LastOrDefault(message => message.IsToast);

    public static NativeMessage? LatestToast(NativeMessage? message) =>
        message is { IsToast: true } ? message : null;
}

/// <summary>
/// The transient toast shown over an activity. A screen offers the newest toast
/// in its presenter state after every update; a message already offered (same
/// <see cref="NativeMessage.Id"/>) is never shown again, so re-renders, state
/// copies and a remounted screen do not bring an expired toast back. A new toast
/// replaces the current one and restarts the timer. <see cref="Changed"/> is
/// raised from the timer when the toast expires, so the screen must marshal to
/// its UI thread.
/// </summary>
public sealed class NativeToastScheduler : IDisposable
{
    /// <summary>Source ShowSuccess/ShowInfo default (ToastService.cs: 3000 ms).</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _timer;
    private long _lastOfferedId;
    private bool _disposed;

    public NativeToastScheduler(TimeProvider? time = null, TimeSpan? duration = null)
    {
        _time = time ?? TimeProvider.System;
        Duration = duration ?? DefaultDuration;
        if (Duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
    }

    public TimeSpan Duration { get; }

    public NativeMessage? Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changes because the toast expired.</summary>
    public event Action? Changed;

    /// <summary>
    /// Shows the message when it is a toast not offered before. Returns true when
    /// <see cref="Current"/> changed. Banners and null are ignored: a toast stays
    /// for its full duration even when the next state has no toast.
    /// </summary>
    public bool Offer(NativeMessage? message)
    {
        if (message is not { IsToast: true })
            return false;
        lock (_gate)
        {
            if (_disposed || message.Id == _lastOfferedId || message.Id < _lastOfferedId)
                return false;
            _lastOfferedId = message.Id;
            Current = message;
            _timer?.Dispose();
            var id = message.Id;
            _timer = _time.CreateTimer(_ => Expire(id), null, Duration, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    /// <summary>The learner closed the toast. Returns true when one was showing.</summary>
    public bool Dismiss()
    {
        lock (_gate)
        {
            if (Current is null)
                return false;
            Current = null;
            _timer?.Dispose();
            _timer = null;
            return true;
        }
    }

    /// <summary>Clears the toast without raising <see cref="Changed"/> (screen closed or replaced).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Current = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Expire(long id)
    {
        lock (_gate)
        {
            // A timer callback already queued when a newer toast replaced it must not clear the newer one.
            if (_disposed || Current?.Id != id)
                return;
            Current = null;
            _timer?.Dispose();
            _timer = null;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Current = null;
            _timer?.Dispose();
            _timer = null;
        }
        Changed = null;
    }
}
