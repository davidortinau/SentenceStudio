using SentenceStudio.Services.Plans;

namespace SentenceStudio.Services;

/// <summary>
/// Single-device <see cref="IUserScopeProvider"/> for the reference and native
/// heads. Generation-bound readiness leases keep an in-flight operation from
/// inheriting another account's active owner after a switch.
/// </summary>
public sealed class DeviceUserScopeProvider : IUserScopeProvider
{
    private readonly object _gate = new();
    private readonly AsyncLocal<ReadinessLease?> _readinessLease = new();
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private string? _activeUserProfileId;
    private long _generation;

    public readonly record struct ScopeStamp(long Generation, string? PublishedOwner);

    public sealed class AuthenticationLease : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _disposed;

        internal AuthenticationLease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _gate.Release();
        }
    }

    public async ValueTask<AuthenticationLease> EnterAuthenticationAsync(
        CancellationToken cancellationToken)
    {
        await _authenticationGate.WaitAsync(cancellationToken);
        return new AuthenticationLease(_authenticationGate);
    }

    public sealed class ReadinessLease : IDisposable
    {
        private readonly DeviceUserScopeProvider _provider;
        private readonly ReadinessLease? _previous;
        private int _disposed;

        internal ReadinessLease(
            DeviceUserScopeProvider provider, string owner, ScopeStamp stamp,
            ReadinessLease? previous)
        {
            _provider = provider;
            Owner = owner;
            Stamp = stamp;
            _previous = previous;
        }

        public string Owner { get; }
        public ScopeStamp Stamp { get; }
        public bool IsCurrent => _provider.IsLeaseCurrent(this);

        internal bool IsRevoked => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            if (ReferenceEquals(_provider._readinessLease.Value, this))
                _provider._readinessLease.Value = _previous;
        }
    }

    /// <summary>
    /// Set by the auth / post-login flow once the active profile is known.
    /// Pass <c>null</c> on sign-out.
    /// </summary>
    public void SetActiveUser(string? userProfileId)
    {
        lock (_gate)
        {
            _activeUserProfileId =
                string.IsNullOrWhiteSpace(userProfileId) ? null : userProfileId;
            _generation = checked(_generation + 1);
        }
    }

    public ScopeStamp CaptureStamp()
    {
        lock (_gate)
            return new ScopeStamp(_generation, _activeUserProfileId);
    }

    public bool TryBeginAuthentication(
        ScopeStamp? previouslyPublished, out ScopeStamp transition)
    {
        lock (_gate)
        {
            if (_activeUserProfileId is not null
                && (previouslyPublished is not { } owned
                    || owned.Generation != _generation
                    || owned.PublishedOwner != _activeUserProfileId))
            {
                transition = default;
                return false;
            }

            if (_activeUserProfileId is null
                && previouslyPublished is { } stale
                && stale.Generation != _generation)
            {
                transition = default;
                return false;
            }

            _activeUserProfileId = null;
            _generation = checked(_generation + 1);
            transition = new ScopeStamp(_generation, null);
            return true;
        }
    }

    public bool IsCurrent(ScopeStamp stamp)
    {
        lock (_gate)
            return stamp.Generation == _generation
                && stamp.PublishedOwner == _activeUserProfileId;
    }

    /// <summary>Leases a current transition or the same owner's published stamp.</summary>
    public ReadinessLease? TryBeginReadiness(string owner, ScopeStamp stamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            if ((stamp.PublishedOwner is { } publishedOwner
                    && !string.Equals(publishedOwner, owner, StringComparison.Ordinal))
                || !IsCurrent(stamp)
                || _readinessLease.Value is not null)
                return null;

            var lease = new ReadinessLease(
                this, owner, stamp, _readinessLease.Value);
            _readinessLease.Value = lease;
            return lease;
        }
    }

    public bool TryPublishAuthenticatedUser(
        string owner, ScopeStamp transition, CancellationToken cancellationToken,
        out ScopeStamp published)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transition.PublishedOwner is not null || !IsCurrent(transition))
            {
                published = default;
                return false;
            }

            _activeUserProfileId = owner;
            _generation = checked(_generation + 1);
            published = new ScopeStamp(_generation, owner);
            return true;
        }
    }

    public bool TryClearIfCurrent(ScopeStamp stamp)
    {
        lock (_gate)
        {
            if (!IsCurrent(stamp))
                return false;

            _activeUserProfileId = null;
            _generation = checked(_generation + 1);
            return true;
        }
    }

    private bool IsLeaseCurrent(ReadinessLease lease)
    {
        lock (_gate)
            return ReferenceEquals(_readinessLease.Value, lease)
                && !lease.IsRevoked && lease.Stamp.Generation == _generation
                && lease.Stamp.PublishedOwner == _activeUserProfileId;
    }

    public string UserProfileId
    {
        get
        {
            if (TryGetUserProfileId(out var id))
            {
                return id;
            }
            throw new UnauthorizedAccessException(
                "No active user profile is set on the device.");
        }
    }

    public bool TryGetUserProfileId(out string userProfileId)
    {
        var lease = _readinessLease.Value;
        lock (_gate)
        {
            if (lease is not null)
            {
                if (IsLeaseCurrent(lease))
                {
                    userProfileId = lease.Owner;
                    return true;
                }

                // A child flow retaining a revoked lease must not inherit
                // a different session's published owner.
                userProfileId = string.Empty;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_activeUserProfileId))
            {
                userProfileId = _activeUserProfileId;
                return true;
            }

            userProfileId = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Only the current readiness operation may use its owner for CoreSync.
    /// A published owner without a lease cannot authorize a request.
    /// </summary>
    public bool TryGetLeasedReadinessOwner(out string userProfileId)
    {
        var lease = _readinessLease.Value;
        lock (_gate)
        {
            if (lease is not null
                && (lease.Stamp.PublishedOwner is null
                    || string.Equals(lease.Stamp.PublishedOwner, lease.Owner, StringComparison.Ordinal))
                && IsLeaseCurrent(lease))
            {
                userProfileId = lease.Owner;
                return true;
            }

            userProfileId = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// A published-owner lease can also be used by an already-ready session.
    /// </summary>
    public bool TryGetPublishedReadinessOwner(out string userProfileId)
    {
        var lease = _readinessLease.Value;
        lock (_gate)
        {
            if (lease is not null
                && string.Equals(lease.Stamp.PublishedOwner, lease.Owner, StringComparison.Ordinal)
                && IsLeaseCurrent(lease))
            {
                userProfileId = lease.Owner;
                return true;
            }

            userProfileId = string.Empty;
            return false;
        }
    }
}
