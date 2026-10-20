using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Contracts;
using SentenceStudio.Data;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeProfileBootstrapTests
{
    [Fact]
    public async Task MatchingAuthenticatedProfile_PersistsInRealIsolatedSqlite()
    {
        using var fixture = new ProfileFixture("owner-a");
        await fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a"));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.AsNoTracking().SingleAsync();
        Assert.Equal("owner-a", profile.Id);
        Assert.Equal("English", profile.NativeLanguage);
        Assert.Equal("Korean", profile.TargetLanguage);
        Assert.Equal(0, await db.DailyPlanCompletions.CountAsync());

        await fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a"));
        Assert.Equal(1, fixture.RequestCount);
        Assert.Equal(1, await db.UserProfiles.CountAsync());
        var route = await fixture.Services
            .GetRequiredService<IPostLoginRouter>()
            .DecideRouteAsync("owner-a");
        Assert.Equal("/", route.Path);
    }

    [Theory]
    [InlineData(null, "Korean", "", "Korean")]
    [InlineData("English", null, "English", "")]
    [InlineData("", "Korean", "", "Korean")]
    [InlineData("English", "", "English", "")]
    [InlineData("   ", "Korean", "", "Korean")]
    public async Task MatchingIncompleteProfile_PersistsWithoutInventingLanguages(
        string? nativeLanguage, string? targetLanguage,
        string expectedNative, string expectedTarget)
    {
        using var fixture = new ProfileFixture("owner-a")
        {
            NativeLanguage = nativeLanguage,
            TargetLanguage = targetLanguage
        };
        await fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a"));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.UserProfiles.AsNoTracking().SingleAsync();
        Assert.Equal("owner-a", profile.Id);
        Assert.Equal(expectedNative, profile.NativeLanguage);
        Assert.Equal(expectedTarget, profile.TargetLanguage);
        Assert.True(string.IsNullOrEmpty(profile.NativeLanguage)
            || string.IsNullOrEmpty(profile.TargetLanguage));
        Assert.Equal(0, await db.DailyPlanCompletions.CountAsync());
        Assert.Equal(1, fixture.RequestCount);
        var route = await fixture.Services
            .GetRequiredService<IPostLoginRouter>()
            .DecideRouteAsync("owner-a");
        Assert.Equal("/onboarding", route.Path);
        Assert.False(route.DeferUntilSyncCompletes);
        Assert.False(route.ShouldMarkOnboarded);
    }

    [Theory]
    [InlineData("English", "Korean")]
    [InlineData(null, null)]
    public async Task ForeignProfile_RefusedWithoutCrossOwnerRow(
        string? nativeLanguage, string? targetLanguage)
    {
        using var fixture = new ProfileFixture("owner-b") {
            NativeLanguage = nativeLanguage,
            TargetLanguage = targetLanguage
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a")));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await db.UserProfiles.CountAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedApi_RefusedWithoutCreatingLocalUser(
        HttpStatusCode responseStatus)
    {
        using var fixture = new ProfileFixture("owner-a") {
            ResponseStatus = responseStatus
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a")));

        using var scope = fixture.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().UserProfiles.CountAsync());
    }

    [Fact]
    public async Task ProfileRequestTimeout_IsReportedAsNetworkErrorWithoutLocalWrite()
    {
        using var fixture = new ProfileFixture("owner-a") {
            SimulateTimeout = true
        };
        await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Bootstrap.EnsureLocalProfileAsync(Token("owner-a")));
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>().UserProfiles.CountAsync());
    }

    private static string Token(string owner)
    {
        var token = new JwtSecurityToken(claims: [
            new Claim(AuthClaimTypes.UserProfileId, owner) ]);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class ProfileFixture : IDisposable
    {
        private readonly string _folder = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "obj", "native-profile-bootstrap-tests",
            Guid.NewGuid().ToString("N")));
        private readonly string _returnedOwner;
        private readonly ServiceProvider _services;

        public ProfileFixture(string returnedOwner)
        {
            _returnedOwner = returnedOwner;
            Directory.CreateDirectory(_folder);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite($"Data Source={Path.Combine(_folder, "profile.sqlite")};Pooling=False"));
            services.AddSingleton<ISyncService, NoOpSyncService>();
            services.AddSingleton<UserProfileRepository>();
            services.AddSingleton<IPostLoginRouter, PostLoginRouter>();
            services.AddHttpClient("AuthClient", client =>
                client.BaseAddress = new Uri("https://api.example.invalid/"))
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new ProfileHandler(this));
            services.AddSingleton<NativeProfileBootstrap>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
        }

        public NativeProfileBootstrap Bootstrap =>
            _services.GetRequiredService<NativeProfileBootstrap>();
        public IServiceProvider Services => _services;
        public int RequestCount { get; private set; }
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public bool SimulateTimeout { get; set; }
        public string? NativeLanguage { get; set; } = "English";
        public string? TargetLanguage { get; set; } = "Korean";

        public void Dispose()
        {
            _services.Dispose();
            Directory.Delete(_folder, recursive: true);
        }

        private sealed class ProfileHandler(ProfileFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                fixture.RequestCount++;
                if (fixture.SimulateTimeout)
                    throw new TaskCanceledException("Scratch API timeout.");
                if (request.RequestUri?.AbsolutePath != "/api/v1/profile/owner-a")
                    throw new InvalidOperationException("Profile fetch changed its owner-scoped route.");
                var body = JsonSerializer.Serialize(new {
                    Id = fixture._returnedOwner, DisplayName = "Canonical Test User",
                    Email = "squad-jayne@sentencestudio.test",
                    fixture.NativeLanguage, fixture.TargetLanguage,
                    DisplayLanguage = "English", TargetCefrLevel = "A1",
                    PreferredSessionMinutes = 20
                });
                return Task.FromResult(new HttpResponseMessage(fixture.ResponseStatus) {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }

        private static string CurrentFile([CallerFilePath] string file = "") => file;
    }
}
