using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
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

        Assert.Single(fixture.LogMessages);
        AssertNoRawAccountData(fixture.LogMessages);
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

    [Theory]
    [InlineData("create")]
    [InlineData("confirm")]
    [InlineData("link")]
    public async Task StartAsync_FailingIdentityResultDoesNotExposeAccountData(
        string failureStage)
    {
        await using var fixture = await DevTestAccountSeederFixture.CreateAsync(failureStage);
        var account = DevTestAccountSeeder.Accounts[0];

        if (failureStage != "create")
        {
            using var scope = fixture.Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = account.Email,
                Email = account.Email,
                DisplayName = account.DisplayName,
                EmailConfirmed = failureStage == "link"
            };
            var result = await userManager.CreateAsync(user, account.Password);
            Assert.True(result.Succeeded, "The preexisting account must be available before seeding.");
        }

        var failure = await Record.ExceptionAsync(
            () => fixture.Seeder.StartAsync(CancellationToken.None));

        Assert.True(failure is InvalidOperationException,
            "A failed identity operation must produce a sanitized seeder exception.");
        var expectedOperation = failureStage switch
        {
            "create" => "could not be created",
            "confirm" => "could not be confirmed",
            _ => "could not be linked to a profile"
        };
        Assert.True(failure.Message.Contains(expectedOperation, StringComparison.Ordinal),
            "The error must identify the operation without including account details.");
        Assert.NotEmpty(fixture.LogMessages);
        AssertNoRawAccountData([failure.Message, .. fixture.LogMessages]);
    }

    private static void AssertNoRawAccountData(IEnumerable<string> outputs)
    {
        foreach (var output in outputs)
        {
            Assert.False(output.Contains('@'), "Seeder output must not contain an account address.");
            foreach (var account in DevTestAccountSeeder.Accounts)
            {
                Assert.False(output.Contains(account.Email, StringComparison.OrdinalIgnoreCase),
                    "Seeder output must not contain an unmasked account address.");
                Assert.False(output.Contains(account.Password, StringComparison.Ordinal),
                    "Seeder output must not contain an account credential.");
            }
        }
    }

    private sealed class DevTestAccountSeederFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CapturingSeederLogger _logger;

        private DevTestAccountSeederFixture(
            SqliteConnection connection,
            ServiceProvider services,
            DevTestAccountSeeder seeder,
            CapturingSeederLogger logger)
        {
            _connection = connection;
            Services = services;
            Seeder = seeder;
            _logger = logger;
        }

        public ServiceProvider Services { get; }

        public DevTestAccountSeeder Seeder { get; }

        public IReadOnlyList<string> LogMessages => _logger.Messages;

        public static async Task<DevTestAccountSeederFixture> CreateAsync(
            string? failureStage = null)
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ToString());
            await connection.OpenAsync();
            var services = new ServiceCollection();

            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
            if (failureStage is not null)
            {
                services.AddSingleton<IUserValidator<ApplicationUser>>(
                    new RejectingSeedValidator(failureStage));
            }
            var logger = new CapturingSeederLogger();
            services.AddSingleton<ILogger<DevTestAccountSeeder>>(logger);

            var provider = services.BuildServiceProvider();

            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var seeder = new DevTestAccountSeeder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<DevTestAccountSeeder>>());

            return new DevTestAccountSeederFixture(connection, provider, seeder, logger);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RejectingSeedValidator(string failureStage) : IUserValidator<ApplicationUser>
    {
        public Task<IdentityResult> ValidateAsync(
            UserManager<ApplicationUser> manager, ApplicationUser user)
        {
            var reject = failureStage switch
            {
                "create" or "confirm" => user.EmailConfirmed && user.UserProfileId is null,
                "link" => user.UserProfileId is not null,
                _ => throw new ArgumentOutOfRangeException(nameof(failureStage))
            };
            return Task.FromResult(reject
                ? IdentityResult.Failed(new IdentityError
                {
                    Code = "SeedFailure",
                    Description = $"{user.Email} {DevTestAccountSeeder.Accounts[0].Password}"
                })
                : IdentityResult.Success);
        }
    }

    private sealed class CapturingSeederLogger : ILogger<DevTestAccountSeeder>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => new EmptyScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class EmptyScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
