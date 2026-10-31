using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SentenceStudio.Api.Tests.Infrastructure;
using SentenceStudio.Contracts.Auth;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// The client password checklist (PasswordPolicy) must equal what the API's
/// ASP.NET Identity configuration actually enforces.
/// </summary>
public class PasswordPolicyParityTests : IClassFixture<JwtBearerApiFactory>
{
    private readonly JwtBearerApiFactory _factory;

    public PasswordPolicyParityTests(JwtBearerApiFactory factory) => _factory = factory;

    [Fact]
    public void ApiIdentityPasswordOptionsEqualSharedPolicy()
    {
        using var scope = _factory.Services.CreateScope();
        var o = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;

        o.RequiredLength.Should().Be(PasswordPolicy.MinimumLength);
        o.RequireUppercase.Should().Be(PasswordPolicy.RequireUppercase);
        o.RequireLowercase.Should().Be(PasswordPolicy.RequireLowercase);
        o.RequireDigit.Should().Be(PasswordPolicy.RequireDigit);
        o.RequireNonAlphanumeric.Should().Be(PasswordPolicy.RequireSymbol);
        o.RequiredUniqueChars.Should().Be(PasswordPolicy.RequiredUniqueChars);
    }

    [Theory]
    [InlineData("Throwaway9Pass")]
    [InlineData("Throwaway9Pass!")]
    [InlineData("throwaway9pass!")]
    [InlineData("THROWAWAY9PASS!")]
    [InlineData("Throwaway!Pass")]
    [InlineData("Tw9!")]
    [InlineData("Tw9!abcd")]
    [InlineData("Tw9 abcd")]
    [InlineData("Passw0rdé")]
    [InlineData("Pass한글123a")]
    [InlineData("PASSWORD123!")]
    [InlineData("")]
    public async Task ClientChecklistAgreesWithServerValidator(string password)
    {
        using var scope = _factory.Services.CreateScope();
        var validator = scope.ServiceProvider
            .GetRequiredService<IEnumerable<IPasswordValidator<ApplicationUser>>>().Single();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await validator.ValidateAsync(users, new ApplicationUser(), password);

        PasswordPolicy.IsSatisfied(password).Should().Be(result.Succeeded);
        foreach (var rule in PasswordPolicy.Rules)
        {
            var failedOnServer = result.Errors.Any(e =>
                PasswordPolicy.RuleForIdentityErrorCode(e.Code) == rule);
            PasswordPolicy.IsMet(rule, password).Should().Be(!failedOnServer,
                $"rule {rule} must agree with the server for '{password}'");
        }
    }

    [Fact]
    public async Task RegisterRejectionCarriesIdentityErrorCodes()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = $"pw-policy-{Guid.NewGuid():N}@test.local",
            Password = "Throwaway9Pass",
            DisplayName = "Policy"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = doc.RootElement.GetProperty("errorCodes").EnumerateArray()
            .Select(c => c.GetString()).ToList();
        codes.Should().Contain("PasswordRequiresNonAlphanumeric");
        PasswordPolicy.ServerErrorKeyFor(codes!).Should().Be("PasswordPolicy_Error_Symbol");
    }

    [Fact]
    public void EveryPolicyResourceKeyExistsInEnglishAndKorean()
    {
        var keys = PasswordPolicy.Rules
            .SelectMany(r => new[] { PasswordPolicy.LabelKey(r), PasswordPolicy.ServerErrorKey(r) })
            .Append(PasswordPolicy.GenericServerErrorKey)
            .ToList();
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "SentenceStudio.Shared", "Resources", "Strings"));
        foreach (var file in new[] { "AppResources.resx", "AppResources.ko.resx" })
        {
            var names = XDocument.Load(Path.Combine(dir, file)).Root!
                .Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet();
            names.Should().Contain(keys, $"{file} must localize every password-policy string");
        }
    }
}
