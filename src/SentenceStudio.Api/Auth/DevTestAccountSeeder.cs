using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
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

        foreach (var account in Accounts)
        {
            await EnsureAccountAsync(userManager, db, account, cancellationToken);
        }

        _logger.LogInformation(
            "DevTestAccountSeeder: {AccountCount} accounts ensured ({Accounts})",
            Accounts.Length,
            string.Join(", ", Accounts.Select(a => a.ShortEmail)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureAccountAsync(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        DevTestAccount account,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByNameAsync(account.Email);
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

            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Dev test account '{account.Email}' could not be created: {errors}");
            }
        }
        else if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errors = string.Join("; ", updateResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Dev test account '{account.Email}' could not be confirmed: {errors}");
            }
        }

        if (!await HasLinkedProfileAsync(db, user.UserProfileId, cancellationToken))
        {
            var profile = await db.UserProfiles.FirstOrDefaultAsync(
                p => p.Email == account.Email,
                cancellationToken);

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
                db.UserProfiles.Add(profile);
                await db.SaveChangesAsync(cancellationToken);
            }

            user.UserProfileId = profile.Id;
            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errors = string.Join("; ", updateResult.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Dev test account '{account.Email}' could not be linked to a profile: {errors}");
            }
        }
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
        public string ShortEmail => Email[..(Email.IndexOf('@', StringComparison.Ordinal) + 1)];
    }
}
