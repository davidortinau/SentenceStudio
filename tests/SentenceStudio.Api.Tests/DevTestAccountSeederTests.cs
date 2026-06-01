using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Api.Auth;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests;

public class DevTestAccountSeederTests
{
    [Fact]
    public async Task StartAsync_CreatesConfirmedAccountsWithProfilesAndCanonicalPasswords()
    {
        await using var fixture = await DevTestAccountSeederFixture.CreateAsync();

        await fixture.Seeder.StartAsync(CancellationToken.None);

        using var scope = fixture.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var account in DevTestAccountSeeder.Accounts)
        {
            var user = await userManager.FindByNameAsync(account.Email);

            user.Should().NotBeNull();
            user!.EmailConfirmed.Should().BeTrue();
            user.DisplayName.Should().Be(account.DisplayName);
            user.UserProfileId.Should().NotBeNullOrWhiteSpace();
            (await userManager.CheckPasswordAsync(user, account.Password)).Should().BeTrue();

            var profile = await db.UserProfiles.SingleAsync(p => p.Id == user.UserProfileId);
            profile.Email.Should().Be(account.Email);
            profile.Name.Should().Be(account.DisplayName);
            profile.NativeLanguage.Should().Be("English");
            profile.TargetLanguage.Should().Be("Korean");
            profile.TargetCEFRLevel.Should().Be("A1");
        }
    }

    [Fact]
    public async Task StartAsync_DoesNotRotateExistingPasswordButRepairsConfirmationAndProfile()
    {
        await using var fixture = await DevTestAccountSeederFixture.CreateAsync();
        const string existingPassword = "Existing123!";
        var account = DevTestAccountSeeder.Accounts.Single(a => a.Email == "captain@test.local");

        using (var scope = fixture.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = account.Email,
                Email = account.Email,
                DisplayName = account.DisplayName,
                EmailConfirmed = false
            };

            var createResult = await userManager.CreateAsync(user, existingPassword);
            createResult.Succeeded.Should().BeTrue();
        }

        await fixture.Seeder.StartAsync(CancellationToken.None);
        await fixture.Seeder.StartAsync(CancellationToken.None);

        using (var scope = fixture.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await userManager.FindByNameAsync(account.Email);

            user.Should().NotBeNull();
            user!.EmailConfirmed.Should().BeTrue();
            user.UserProfileId.Should().NotBeNullOrWhiteSpace();
            (await userManager.CheckPasswordAsync(user, account.Password)).Should().BeFalse();
            (await userManager.CheckPasswordAsync(user, existingPassword)).Should().BeTrue();
            (await db.UserProfiles.CountAsync(p => p.Email == account.Email)).Should().Be(1);
        }
    }

    private sealed class DevTestAccountSeederFixture : IAsyncDisposable
    {
        private readonly string _dbPath;

        private DevTestAccountSeederFixture(
            string dbPath,
            ServiceProvider services,
            DevTestAccountSeeder seeder)
        {
            _dbPath = dbPath;
            Services = services;
            Seeder = seeder;
        }

        public ServiceProvider Services { get; }

        public DevTestAccountSeeder Seeder { get; }

        public static async Task<DevTestAccountSeederFixture> CreateAsync()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"sentencestudio_devseed_{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite($"Data Source={dbPath}"));
            services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var seeder = new DevTestAccountSeeder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<DevTestAccountSeeder>>());

            return new DevTestAccountSeederFixture(dbPath, provider, seeder);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }
}
