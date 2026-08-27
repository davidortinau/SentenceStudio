using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// A user scope whose answer the test controls, including the answer "there is nobody here".
/// </summary>
/// <remarks>
/// The production implementation reads a claim off the request. The interesting cases for the
/// write ledger are the ones a request cannot easily produce: an empty scope, and two scopes that
/// differ. Both are trivial to express here and awkward to express through the pipeline.
/// </remarks>
public sealed class FakeUserScope : IUserScopeProvider
{
    private readonly string? _userProfileId;

    /// <summary>How many times anything asked who the user is.</summary>
    public int Reads { get; private set; }

    public FakeUserScope(string? userProfileId) => _userProfileId = userProfileId;

    public string UserProfileId
    {
        get
        {
            Reads++;
            if (string.IsNullOrWhiteSpace(_userProfileId))
            {
                throw new UnauthorizedAccessException("No user profile is in scope.");
            }

            return _userProfileId;
        }
    }

    public bool TryGetUserProfileId(out string userProfileId)
    {
        Reads++;
        userProfileId = _userProfileId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(_userProfileId);
    }
}

/// <summary>
/// An <see cref="ApplicationDbContext"/> that counts the queries sent to the database.
/// </summary>
/// <remarks>
/// "Fails closed before any query" is a claim about what did not happen, and a test that only
/// checks the thrown exception cannot tell a guard that ran first from a guard that ran after the
/// database had already been asked about another learner's rows. Counting commands makes the
/// absence observable.
/// </remarks>
public sealed class QueryCountingInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    public int Commands { get; private set; }

    public override System.Data.Common.DbCommand CommandCreated(
        Microsoft.EntityFrameworkCore.Diagnostics.CommandEndEventData eventData,
        System.Data.Common.DbCommand result)
    {
        Commands++;
        return base.CommandCreated(eventData, result);
    }
}

/// <summary>
/// Builds a write ledger over a live coach database with the handlers the test needs.
/// </summary>
internal static class CoachWriteTestScope
{
    /// <summary>Registry with both Sam feature switches on, frozen as production freezes it.</summary>
    public static CoachToolRegistry EnabledRegistry()
    {
        var registry = new CoachToolRegistry(new CoachOptions
        {
            DurableHistory = new CoachFeatureSwitch { Enabled = true },
            SamOverlay = new CoachFeatureSwitch { Enabled = true },
            SamReadTools = new CoachFeatureSwitch { Enabled = true },
            SamWriteTools = new CoachFeatureSwitch { Enabled = true }
        });

        registry.Freeze();
        return registry;
    }

    public static CoachWriteOperationService NewLedger(
        CoachDbContext db,
        ICoachContentProtector protector,
        IEnumerable<ICoachWriteHandler> handlers,
        IUserScopeProvider scope,
        TimeProvider time,
        ICoachToolRegistry? registry = null,
        SentenceStudio.Api.Coach.Opportunities.ICoachOpportunityRecorder? opportunities = null) =>
        new(
            db,
            protector,
            new CoachWriteHandlerCatalog(handlers),
            registry ?? EnabledRegistry(),
            scope,
            time,
            NullLogger<CoachWriteOperationService>.Instance,
            opportunities);
}
