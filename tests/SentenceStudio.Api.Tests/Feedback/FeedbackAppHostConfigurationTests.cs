using System.Text.Json;
using System.Text.RegularExpressions;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// Holds the shipped AppHost input and generated-manifest contract for the feedback signing key.
/// </summary>
public sealed class FeedbackAppHostConfigurationTests
{
    private const string ParameterName = "feedbackhmackey";
    private const string EnvironmentName = "Feedback__HmacKey";

    [Fact]
    public void The_manifest_declares_a_dedicated_secret_parameter()
    {
        var source = AppHostSource();

        source.Should().Contain(
            """var feedbackhmackey = builder.AddParameter("feedbackhmackey", secret: true);""",
            "the manifest must ask deployment for a secret rather than embedding a signing key");
        CountOccurrences(source, ParameterName).Should().Be(4,
            "the canonical name should appear only in its deployment comment, declaration "
            + "identifier and literal, and API forwarding expression");
        source.Should().NotContain(
            """AddParameter("feedbackhmackey", value:""",
            "a default would serialize signing material into shipped configuration");
    }

    [Fact]
    public void The_secret_reference_is_forwarded_only_to_the_api()
    {
        var source = AppHostSource();
        const string forwarding =
            """.WithEnvironment("Feedback__HmacKey", feedbackhmackey)""";

        CountOccurrences(source, EnvironmentName).Should().Be(1,
            "the feedback signing key belongs only in the API process");
        source.Should().Contain(forwarding,
            "passing the parameter resource makes Aspire emit a secret reference, not its value");
        Regex.IsMatch(
                source,
                "WithEnvironment\\(\"Feedback__HmacKey\",\\s*\"[^\"]+",
                RegexOptions.CultureInvariant)
            .Should().BeFalse("the AppHost must never contain an inline feedback signing key");

        var apiStart = source.IndexOf(
            """var api = builder.AddProject<SentenceStudio_Api>("api")""",
            StringComparison.Ordinal);
        var webAppStart = source.IndexOf(
            """var webapp = builder.AddProject<SentenceStudio_WebApp>("webapp")""",
            StringComparison.Ordinal);
        var forwardingIndex = source.IndexOf(forwarding, StringComparison.Ordinal);

        apiStart.Should().BeGreaterThanOrEqualTo(0);
        webAppStart.Should().BeGreaterThan(apiStart);
        forwardingIndex.Should().BeGreaterThan(apiStart).And.BeLessThan(webAppStart,
            "the one secret reference must be part of the API resource definition");
    }

    [Fact]
    public void The_canonical_parameter_name_maps_to_the_expected_azd_input()
    {
        var azdInput = $"AZURE_{ParameterName.ToUpperInvariant()}";

        azdInput.Should().Be("AZURE_FEEDBACKHMACKEY");
    }

    [Fact]
    public void The_shipped_secret_template_contains_only_a_placeholder()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "secrets.template.json")));

        var configuredValue = document.RootElement
            .GetProperty($"Parameters:{ParameterName}")
            .GetString();

        configuredValue.Should().Be(
            "<32+ character random value, distinct from Jwt:SigningKey>");
        configuredValue.Should().StartWith("<").And.EndWith(">",
            "the repository must document the input without shipping signing material");
    }

    private static string AppHostSource() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "SentenceStudio.AppHost", "AppHost.cs"));

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        return root?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
