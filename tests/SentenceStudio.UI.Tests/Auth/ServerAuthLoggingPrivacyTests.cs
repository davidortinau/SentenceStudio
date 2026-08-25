using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebApp.Auth;

namespace SentenceStudio.UI.Tests.Auth;

/// <summary>
/// Runs the real <see cref="ServerAuthService"/> against a real Identity stack and reads back
/// every log record it produced — message, structured state, scopes and exception — asserting the
/// account's email address appears in none of them.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the previous attempt did not have. That change asserted the masking *function*
/// and stopped, so the four call sites it touched were verified and the sixteen it didn't were
/// not. Asserting on the function proves the function; only running the flow proves the flow.
/// </para>
/// <para>
/// Registration failure is the case that matters most: Identity's <c>DuplicateUserName</c>
/// describer builds the sentence "User name 'someone@example.com' is already taken", so any log
/// that renders a description — or an <c>IdentityResult</c> whose <c>ToString</c> reaches one —
/// prints the address of somebody who already has an account.
/// </para>
/// </remarks>
public sealed class ServerAuthLoggingPrivacyTests : IAsyncLifetime
{
    /// <summary>
    /// The canonical Squad test account. Every assertion below looks for this string and for its
    /// local part alone, because <c>squad-jayne</c> on its own is still an identifier.
    /// </summary>
    private const string Email = "squad-jayne@sentencestudio.test";

    private const string LocalPart = "squad-jayne";
    private const string DisplayName = "Jayne Cobb";
    private const string Password = "SquadTest!2026";

    private SqliteConnection _connection = null!;
    private Microsoft.Extensions.DependencyInjection.ServiceProvider _provider = null!;
    private RecordingLoggerProvider _records = null!;
    private StubHttpContextAccessor _http = null!;
    private readonly DeleteFailureSwitch _deleteFailure = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        _records = new RecordingLoggerProvider();
        _http = new StubHttpContextAccessor();

        var services = new ServiceCollection();

        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(_records);
        });

        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));

        services
            .AddIdentityCore<ApplicationUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = false;
                o.Password.RequiredLength = 8;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme);

        services.AddSingleton<IHttpContextAccessor>(_http);
        services.AddSingleton<IWebHostEnvironment>(new StubWebHostEnvironment());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_deleteFailure);
        services.AddScoped<UserManager<ApplicationUser>, FailableUserManager>();

        _provider = services.BuildServiceProvider();

        // SignInManager.SignOutAsync resolves the authentication service off the request, so the
        // delete path only runs end-to-end when the context can see the container.
        _http.HttpContext!.RequestServices = _provider;

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Registering_an_address_that_already_exists_does_not_log_the_address()
    {
        var auth = CreateService();

        (await auth.RegisterAsync(Email, Password, DisplayName)).Should().NotBeNull();
        _records.Clear();

        // Identity rejects this with DuplicateUserName, whose Description is the address in a
        // sentence. The thrown message still carries it — RegisterPage shows that to the person
        // who typed it — but nothing may reach the log.
        var duplicate = async () => await auth.RegisterAsync(Email, Password, DisplayName);
        await duplicate.Should().ThrowAsync<InvalidOperationException>();

        AssertNothingLeaked();

        // Non-vacuous: the failure was actually logged, and the operator can still tell what went
        // wrong. A test that passes because nothing was written proves nothing.
        var warning = _records.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Rendered.Should().Contain("Registration failed");
        warning.Rendered.Should().Contain("DuplicateUserName");
        warning.Rendered.Should().Contain("squ***@sentencestudio.test");
    }

    [Fact]
    public async Task A_successful_registration_logs_the_user_id_rather_than_the_address()
    {
        var auth = CreateService();

        await auth.RegisterAsync(Email, Password, DisplayName);

        AssertNothingLeaked();

        var userId = await FindUserIdAsync();
        _records.Entries.Should().Contain(e => e.Rendered.Contains(userId, StringComparison.Ordinal),
            "the stable Identity id is the join key an operator needs, and it is not personal data");
    }

    [Fact]
    public async Task A_failed_sign_in_does_not_log_the_address_that_was_tried()
    {
        var auth = CreateService();
        await auth.RegisterAsync(Email, Password, DisplayName);
        _records.Clear();

        var result = await auth.SignInAsync(Email, "wrong-password-entirely");

        result.Should().BeNull();
        AssertNothingLeaked();
        _records.Entries.Should().Contain(e => e.Rendered.Contains("Sign-in failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_password_change_logs_error_codes_and_not_the_account()
    {
        var auth = CreateService();
        await auth.RegisterAsync(Email, Password, DisplayName);
        await SignInAsync();
        _records.Clear();

        var change = async () => await auth.ChangePasswordAsync("not-the-current-password", "AnotherPass!2026");
        await change.Should().ThrowAsync<InvalidOperationException>();

        AssertNothingLeaked();

        var warning = _records.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Rendered.Should().Contain("Password change failed");
        warning.Rendered.Should().Contain("PasswordMismatch");
    }

    [Fact]
    public async Task A_successful_password_change_logs_the_user_id_rather_than_the_account()
    {
        var auth = CreateService();
        await auth.RegisterAsync(Email, Password, DisplayName);
        await SignInAsync();
        _records.Clear();

        (await auth.ChangePasswordAsync(Password, "AnotherPass!2026")).Should().BeTrue();

        AssertNothingLeaked();

        var userId = await FindUserIdAsync();
        _records.Entries.Should().Contain(e =>
            e.Rendered.Contains("Password changed", StringComparison.Ordinal)
            && e.Rendered.Contains(userId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_successful_delete_logs_the_user_id_rather_than_the_account()
    {
        var auth = CreateService();
        await auth.RegisterAsync(Email, Password, DisplayName);
        await SignInAsync();

        var userId = await FindUserIdAsync();
        _records.Clear();

        (await auth.DeleteAccountAsync()).Should().BeTrue();

        AssertNothingLeaked();
        _records.Entries.Should().Contain(e =>
            e.Rendered.Contains("Deleted Identity account", StringComparison.Ordinal)
            && e.Rendered.Contains(userId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_delete_logs_error_codes_and_not_the_account()
    {
        var auth = CreateService();
        await auth.RegisterAsync(Email, Password, DisplayName);
        await SignInAsync();

        // Identity's delete only fails on a store-level fault, and the service re-reads the user
        // inside its own scope, so there is no way to arrange one from out here. The manager below
        // returns the real describer's ConcurrencyFailure — the same IdentityResult a genuine
        // stamp mismatch produces — so the branch under test, and the line it logs, are the
        // production ones.
        _deleteFailure.Enabled = true;
        _records.Clear();

        var deleted = await auth.DeleteAccountAsync();

        deleted.Should().BeFalse();
        AssertNothingLeaked();

        var warning = _records.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Rendered.Should().Contain("Failed to delete Identity account");
        warning.Rendered.Should().Contain("ConcurrencyFailure");
    }

    /// <summary>
    /// Checks every surface of every record, not just the rendered message. A structured sink
    /// ships the state dictionary and the scope stack as their own fields, so a template that
    /// renders <c>squ***@…</c> while passing the raw value as the argument still writes the
    /// address to storage — the rendered string is the one place the leak would not show.
    /// </summary>
    private void AssertNothingLeaked()
    {
        _records.Entries.Should().NotBeEmpty("a test that inspects no records asserts nothing");

        foreach (var entry in _records.Entries)
        {
            foreach (var (surface, text) in entry.Surfaces())
            {
                text.Should().NotContain(Email,
                    "{0} of '{1}' must not carry the address", surface, entry.Rendered);
                text.Should().NotContain(LocalPart,
                    "{0} of '{1}' must not carry the local part on its own", surface, entry.Rendered);
                text.Should().NotContain(DisplayName,
                    "{0} of '{1}' must not carry the display name", surface, entry.Rendered);
            }
        }
    }

    private ServerAuthService CreateService() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _http,
        // No circuit is running in these tests: every case here signs in through HttpContext, so
        // the circuit tier stays empty and the HTTP tier is the one under test.
        new SentenceStudio.WebApp.Platform.CircuitUserStateAccessor(),
        _provider.GetRequiredService<IConfiguration>(),
        _provider.GetRequiredService<ILogger<ServerAuthService>>());

    /// <summary>
    /// ServerAuthService reads the signed-in account from <c>HttpContext.User.Identity.Name</c>,
    /// which in this application is the address.
    /// </summary>
    private Task SignInAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, Email)], "TestAuth");
        _http.HttpContext!.User = new ClaimsPrincipal(identity);
        return Task.CompletedTask;
    }

    private async Task<string> FindUserIdAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.UserName == Email).Select(u => u.Id).SingleAsync();
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
    }

    /// <summary>
    /// Lets one test ask the manager to fail a delete, without changing behaviour for the others.
    /// </summary>
    private sealed class DeleteFailureSwitch
    {
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// The real <see cref="UserManager{TUser}"/> in every respect except that a delete can be made
    /// to return the describer's own <c>ConcurrencyFailure</c> — the result a genuine stamp
    /// mismatch produces, which cannot be arranged from outside the service's scope.
    /// </summary>
    private sealed class FailableUserManager : UserManager<ApplicationUser>
    {
        private readonly DeleteFailureSwitch _switch;
        private readonly IdentityErrorDescriber _describer;

        public FailableUserManager(
            DeleteFailureSwitch failureSwitch,
            IUserStore<ApplicationUser> store,
            IOptions<IdentityOptions> options,
            IPasswordHasher<ApplicationUser> passwordHasher,
            IEnumerable<IUserValidator<ApplicationUser>> userValidators,
            IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<ApplicationUser>> logger)
            : base(store, options, passwordHasher, userValidators, passwordValidators,
                keyNormalizer, errors, services, logger)
        {
            _switch = failureSwitch;
            _describer = errors;
        }

        public override Task<IdentityResult> DeleteAsync(ApplicationUser user)
            => _switch.Enabled
                ? Task.FromResult(IdentityResult.Failed(_describer.ConcurrencyFailure()))
                : base.DeleteAsync(user);
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } =
            new NullFileProvider();
        public string ApplicationName { get; set; } = "SentenceStudio.UI.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        // Production, deliberately: development branches are permitted to log more, and a test
        // that ran as Development would be measuring the lenient path.
        public string EnvironmentName { get; set; } = "Production";
    }
}
