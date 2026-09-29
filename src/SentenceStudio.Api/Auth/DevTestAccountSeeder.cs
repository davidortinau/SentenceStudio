using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Diagnostics;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Auth;

public sealed class DevTestAccountSeeder : IHostedService
{
    public static readonly DevTestAccount[] Accounts =
    [
        new("captain@test.local", "Captain1!", "Captain"),
        new("testsailor@test.local", "TestPass123!", "Test Sailor"),
        new("e2e@test.local", "E2E1234!", "E2E Tester")
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DevTestAccountSeeder> _logger;

    public DevTestAccountSeeder(
        IServiceScopeFactory scopeFactory,
        ILogger<DevTestAccountSeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await SeedAsync(userManager, db, static () => { }, cancellationToken);
    }

    /// <summary>
    /// Allows the API's pre-listen path to inject an Identity store and DbContext
    /// which both use its locked PostgreSQL connection.
    /// </summary>
    public async Task SeedAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        Action assertSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(assertSession);

        for (var index = 0; index < Accounts.Length; index++)
        {
            var account = Accounts[index];
            cancellationToken.ThrowIfCancellationRequested();
            assertSession();
            await EnsureAccountAsync(
                userManager, db, account, index + 1, assertSession, cancellationToken);
            assertSession();
        }

        _logger.LogInformation(
            "DevTestAccountSeeder: {AccountCount} accounts ensured",
            Accounts.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureAccountAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        DevTestAccount account,
        int accountOrdinal,
        Action assertSession,
        CancellationToken cancellationToken)
    {
        assertSession();
        var user = await userManager.FindByNameAsync(account.Email);
        assertSession();
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = account.Email,
                Email = account.Email,
                DisplayName = account.DisplayName,
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString()
            };
            user.PasswordHash = userManager.PasswordHasher.HashPassword(user, account.Password);

            assertSession();
            var createResult = await userManager.CreateAsync(user);
            assertSession();
            if (!createResult.Succeeded)
            {
                throw SeedFailure(accountOrdinal, "created");
            }
        }
        else if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            assertSession();
            var updateResult = await userManager.UpdateAsync(user);
            assertSession();
            if (!updateResult.Succeeded)
            {
                throw SeedFailure(accountOrdinal, "confirmed");
            }
        }

        assertSession();
        var hasLinkedProfile = await HasLinkedProfileAsync(db, user.UserProfileId, cancellationToken);
        assertSession();
        if (!hasLinkedProfile)
        {
            var profile = await db.UserProfiles.FirstOrDefaultAsync(
                p => p.Email == account.Email,
                cancellationToken);
            assertSession();

            if (profile is null)
            {
                profile = new UserProfile
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = account.DisplayName,
                    Email = account.Email,
                    NativeLanguage = "English",
                    TargetLanguage = "Korean",
                    TargetCEFRLevel = "A1",
                    CreatedAt = DateTime.UtcNow
                };
                assertSession();
                db.UserProfiles.Add(profile);
                await db.SaveChangesAsync(cancellationToken);
                assertSession();
            }

            user.UserProfileId = profile.Id;
            assertSession();
            var updateResult = await userManager.UpdateAsync(user);
            assertSession();
            if (!updateResult.Succeeded)
            {
                throw SeedFailure(accountOrdinal, "linked to a profile");
            }
        }
    }

    private InvalidOperationException SeedFailure(int ordinal, string operation)
    {
        _logger.LogError(
            "DevTestAccountSeeder: account #{AccountOrdinal} could not be {Operation}",
            ordinal, operation);
        return new InvalidOperationException(
            $"Dev test account #{ordinal} could not be {operation}.");
    }

    private static async Task<bool> HasLinkedProfileAsync(
        ApplicationDbContext db,
        string? userProfileId,
        CancellationToken cancellationToken)
    {
        return !string.IsNullOrWhiteSpace(userProfileId)
            && await db.UserProfiles.AnyAsync(p => p.Id == userProfileId, cancellationToken);
    }

    public sealed record DevTestAccount(string Email, string Password, string DisplayName)
    {
        /// <summary>
        /// The account address reduced for logging.
        /// </summary>
        /// <remarks>
        /// This previously kept the whole local part and threw the domain away — exactly inverted,
        /// since the local part is the identifying half. It now uses the same rule as every other
        /// auth log line so a seeded account reads the same way here as it does at sign-in.
        /// </remarks>
        public string ShortEmail => AuthLogRedaction.MaskEmail(Email);
    }
}
