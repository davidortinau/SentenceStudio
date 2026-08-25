using System.Net.Http.Json;
using SentenceStudio.Api.Auth;
using SentenceStudio.Shared.Diagnostics;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// Drives the real auth endpoints against a real host and asserts on what they actually logged.
/// These are deliberately runtime tests rather than source assertions: a source guard proves a
/// raw identifier was not passed to a logger call, but only a runtime test proves the value that
/// did reach the sink is masked, on every surface (message, structured attributes, scopes,
/// exception).
/// </summary>
public sealed class AuthEndpointsLoggingPrivacyTests : IClassFixture<AuthLoggingApiFactory>
{
    // A stable, obviously-synthetic sentinel. The local part is long enough that a correct
    // 3-character-prefix mask still hides most of it, and distinctive enough that a substring
    // search cannot match it by accident.
    private const string Email = "squad-jayne@sentencestudio.test";
    private const string LocalPart = "squad-jayne";
    private const string Password = "SquadTest!2026";
    private const string DisplayName = "Jayne Cobb";

    private readonly AuthLoggingApiFactory _factory;

    public AuthEndpointsLoggingPrivacyTests(AuthLoggingApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Registering_then_re_registering_never_logs_the_address()
    {
        var client = _factory.CreateClient();
        var before = _factory.Recorder.Entries.Count;

        var first = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(Email, Password, DisplayName));
        first.IsSuccessStatusCode.Should().BeTrue("registration must still succeed unchanged");

        // The duplicate attempt is the interesting one: it is the path that produces
        // IdentityError.DuplicateUserName, whose Description interpolates the address.
        var second = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(Email, Password, DisplayName));
        second.IsSuccessStatusCode.Should().BeTrue(
            "the endpoint deliberately returns the generic success to avoid user enumeration");

        AssertClean(before);
    }

    [Fact]
    public async Task A_login_for_an_unknown_address_does_not_log_the_address()
    {
        var client = _factory.CreateClient();
        var before = _factory.Recorder.Entries.Count;

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("squad-nobody@sentencestudio.test", Password));
        response.IsSuccessStatusCode.Should().BeFalse();

        AssertNoText(before, "squad-nobody@sentencestudio.test", "the unknown address");
        AssertNoText(before, "squad-nobody", "the unknown local part");
    }

    [Fact]
    public async Task A_successful_login_logs_identifiers_not_the_address()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(Email, Password, DisplayName));

        var before = _factory.Recorder.Entries.Count;
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(Email, Password));
        response.IsSuccessStatusCode.Should().BeTrue("login behaviour must be unchanged");

        AssertClean(before);
    }

    [Fact]
    public async Task Forgot_password_masks_the_recipient_attribute()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(Email, Password, DisplayName));

        var before = _factory.Recorder.Entries.Count;
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new ForgotPasswordRequest(Email));
        response.IsSuccessStatusCode.Should().BeTrue();

        var emitted = Since(before);

        // Every structured Email attribute must carry the mask, never the address. This is the
        // attribute that ships to the log sink as its own indexed field.
        foreach (var entry in emitted)
        {
            entry.State.Should().NotContain($"Email={Email}",
                "the structured Email attribute must be masked, not raw");
        }

        // The single tolerated exception is the development-only reset link, which has to remain
        // clickable and therefore carries the address in its query string. Nothing else may.
        foreach (var entry in emitted)
        {
            foreach (var (name, text) in entry.Surfaces())
            {
                if (!text.Contains(Email, StringComparison.OrdinalIgnoreCase))
                    continue;

                text.Should().Contain("Reset URL",
                    $"{name} of '{Truncate(text)}' carries the address but is not the " +
                    "development-only reset link, which is the only allowed exception");
            }
        }

        // And the masked form must actually be present, so this test fails if the endpoint stops
        // logging altogether rather than silently passing on an empty set.
        emitted.Should().Contain(
            e => e.State.Contains(AuthLogRedaction.MaskEmail(Email)),
            "the reset flow should still log a masked recipient for diagnostics");
    }

    [Fact]
    public async Task Reset_password_with_a_bad_token_does_not_log_the_address()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(Email, Password, DisplayName));

        var before = _factory.Recorder.Entries.Count;
        var response = await client.PostAsJsonAsync("/api/auth/reset-password",
            new ResetPasswordRequest(Email, "not-a-real-token", "AnotherPass!2026"));
        response.IsSuccessStatusCode.Should().BeFalse("an invalid token must still be rejected");

        AssertClean(before);
    }

    private IReadOnlyList<RecordedAuthLog> Since(int index) =>
        _factory.Recorder.Entries.Skip(index).ToList();

    private void AssertClean(int before)
    {
        AssertNoText(before, Email, "the address");
        AssertNoText(before, LocalPart, "the address local part");
        AssertNoText(before, DisplayName, "the display name");
    }

    private void AssertNoText(int before, string forbidden, string description)
    {
        var emitted = Since(before);
        emitted.Should().NotBeEmpty("the flow under test must actually log something, " +
            "otherwise this assertion passes vacuously");

        foreach (var entry in emitted)
        {
            foreach (var (name, text) in entry.Surfaces())
            {
                text.Should().NotContainEquivalentOf(forbidden,
                    $"{name} of '{Truncate(text)}' (category {entry.Category}) must not carry {description}");
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 160 ? value : value[..160] + "...";
}
