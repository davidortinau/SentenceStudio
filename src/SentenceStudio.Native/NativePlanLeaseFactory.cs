using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;

namespace SentenceStudio.Native;

/// <summary>
/// Captures the owner before resolving a scoped PlanService. The existing
/// DeviceUserScopeProvider is mutable: an account switch between checking a
/// card and MarkCompleteAsync could otherwise mark the next owner's card,
/// because deterministic plan item IDs can coincide across owners.
/// </summary>
public sealed class NativePlanLeaseFactory(IServiceScopeFactory scopes)
{
    internal NativePlanLease ForOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var scope = scopes.CreateScope();
        try
        {
            var services = scope.ServiceProvider;
            var date = services.GetRequiredService<IPlanDateContext>();
            var db = services.GetRequiredService<ApplicationDbContext>();
            var plan = new PlanService(
                db,
                new FixedOwnerScope(owner),
                date,
                services.GetRequiredService<IDeterministicPlanGenerator>(),
                services.GetRequiredService<IPlanCopyProvider>(),
                services.GetRequiredService<ILogger<PlanService>>(),
                services.GetService<ILlmPlanGenerator>());
            return new NativePlanLease(scope, db, plan, date.UserLocalDate);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private sealed class FixedOwnerScope(string owner) : IUserScopeProvider
    {
        public string UserProfileId => owner;

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = owner;
            return true;
        }
    }
}

internal sealed class NativePlanLease(
    IServiceScope scope, ApplicationDbContext db, PlanService plan, DateOnly localDate) : IDisposable
{
    public ApplicationDbContext Database => db;
    public IPlanService Service => plan;
    public DateOnly LocalDate => localDate;

    public Task<TodaysPlanDto> GenerateNativeTodayAsync(CancellationToken cancellationToken) =>
        plan.GenerateNativeTodayAsync(cancellationToken);

    public void Dispose() => scope.Dispose();
}
