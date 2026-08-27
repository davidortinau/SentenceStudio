using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Tests.Coach.Postgres;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// What may and may not reach a public GitHub issue as "client metadata".
/// </summary>
/// <remarks>
/// The disclosure this family guards is the one the old <c>CurrentRoute</c> string caused: a route
/// is not a label, it is a structured value carrying entity identifiers, query strings, and
/// sometimes text the learner typed — all of it copied verbatim into a public repository. Escaping
/// does not help, because the problem is not markup injection; it is that the value should never
/// have left the server at all.
/// </remarks>
public sealed class FeedbackClientMetadataNormalizerTests
{
    // ---------------------------------------------------------------------------- route

    [Theory]
    [InlineData(FeedbackRouteCategory.Dashboard)]
    [InlineData(FeedbackRouteCategory.Activity)]
    [InlineData(FeedbackRouteCategory.Account)]
    [InlineData(FeedbackRouteCategory.Unknown)]
    public void A_declared_route_category_survives(FeedbackRouteCategory category)
    {
        FeedbackClientMetadataNormalizer.NormalizeRoute(category).Should().Be(category);
    }

    /// <summary>
    /// An ordinal nobody declared becomes Unknown.
    /// </summary>
    /// <remarks>
    /// The enum on the wire is a C# type, not a guarantee about bytes.
    /// <c>{"routeCategory": 4210}</c> deserialises happily, and its <c>ToString()</c> prints
    /// <c>4210</c> into the issue body. That is a small leak on its own; what it really shows is
    /// that the contract is not the boundary — this is.
    /// </remarks>
    [Theory]
    [InlineData(4210)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void An_undeclared_route_ordinal_becomes_unknown(int ordinal)
    {
        FeedbackClientMetadataNormalizer.NormalizeRoute((FeedbackRouteCategory)ordinal)
            .Should().Be(FeedbackRouteCategory.Unknown);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-5)]
    public void An_undeclared_platform_ordinal_becomes_unknown(int ordinal)
    {
        FeedbackClientMetadataNormalizer.NormalizePlatform((FeedbackPlatform)ordinal)
            .Should().Be(FeedbackPlatform.Unknown);
    }

    // -------------------------------------------------------------------------- version

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1", "1")]
    [InlineData("10.4", "10.4")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3-preview", "1.2.3-preview")]
    [InlineData("1.2.3-preview.4", "1.2.3-preview.4")]
    [InlineData("  1.2.3  ", "1.2.3")]
    public void A_plain_version_survives(string input, string expected)
    {
        FeedbackClientMetadataNormalizer.NormalizeVersion(input).Should().Be(expected);
    }

    /// <summary>Build metadata is stripped rather than published.</summary>
    /// <remarks>
    /// .NET's informational version is <c>1.2.3+&lt;sha&gt;</c>. The hash is unbounded in the
    /// contract and useless for triage, and every extra unbounded field published verbatim is one
    /// more thing to have to reason about.
    /// </remarks>
    [Fact]
    public void Build_metadata_is_stripped()
    {
        FeedbackClientMetadataNormalizer
            .NormalizeVersion("1.2.3+8f2c9a1b4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f90")
            .Should().Be("1.2.3");
    }

    /// <summary>
    /// A version that is not a version is replaced wholesale, not truncated.
    /// </summary>
    /// <remarks>
    /// Truncation is a length, not a privacy property: the first 32 characters of an email address
    /// or a file path are still an email address or a file path. The field is either the shape it
    /// claims to be, or it is <c>unknown</c>.
    /// </remarks>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("learner@example.com")]
    [InlineData("/Users/someone/Library/Containers/app.db")]
    [InlineData("](https://evil.example/)[")]
    [InlineData("1.2.3 and also my password is hunter2")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_a_version_becomes_unknown(string? input)
    {
        FeedbackClientMetadataNormalizer.NormalizeVersion(input)
            .Should().Be(FeedbackClientMetadataNormalizer.UnknownVersion);
    }

    [Fact]
    public void An_over_long_version_becomes_unknown()
    {
        var long_ = string.Join('.', Enumerable.Repeat("1", 40));

        FeedbackClientMetadataNormalizer.NormalizeVersion(long_)
            .Should().Be(FeedbackClientMetadataNormalizer.UnknownVersion);
    }

    [Fact]
    public void A_normalized_version_never_exceeds_the_column_it_is_stored_in()
    {
        var candidates = new[]
        {
            "1.2.3", "99999.99999.99999.99999", "1.2.3-preview.10.20",
            new string('9', 200), "1.2.3+" + new string('a', 500)
        };

        foreach (var candidate in candidates)
        {
            FeedbackClientMetadataNormalizer.NormalizeVersion(candidate).Length
                .Should().BeLessThanOrEqualTo(FeedbackClientMetadataNormalizer.MaxVersionLength);
        }
    }

    // ------------------------------------------------------------------------ timestamp

    /// <summary>
    /// The client's clock is truncated to the minute.
    /// </summary>
    /// <remarks>
    /// A 100-nanosecond timestamp published verbatim is effectively a unique marker for that
    /// submission, and it is not evidence of anything — it is whatever the client said. The minute
    /// is what a triager reads.
    /// </remarks>
    [Fact]
    public void A_timestamp_is_truncated_to_the_minute()
    {
        var precise = new DateTime(2026, 8, 21, 14, 37, 42, 913, DateTimeKind.Utc).AddTicks(4567);

        FeedbackClientMetadataNormalizer.NormalizeTimestamp(precise)
            .Should().Be(new DateTime(2026, 8, 21, 14, 37, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2200)]
    public void An_implausible_timestamp_is_dropped(int year)
    {
        FeedbackClientMetadataNormalizer
            .NormalizeTimestamp(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().BeNull();
    }

    [Fact]
    public void A_null_timestamp_stays_null()
    {
        FeedbackClientMetadataNormalizer.NormalizeTimestamp(null).Should().BeNull();
    }

    // ----------------------------------------------------------------------------- whole

    [Fact]
    public void Null_metadata_normalizes_to_the_empty_value()
    {
        FeedbackClientMetadataNormalizer.Normalize(null).Should().Be(NormalizedClientMetadata.Empty);
        NormalizedClientMetadata.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_hostile_metadata_block_normalizes_to_nothing_usable()
    {
        var hostile = new ClientMetadata
        {
            AppVersion = "/Users/learner/Documents/private.txt",
            Platform = (FeedbackPlatform)77,
            RouteCategory = (FeedbackRouteCategory)9001,
            Timestamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var normalized = FeedbackClientMetadataNormalizer.Normalize(hostile);

        normalized.Should().Be(NormalizedClientMetadata.Empty);
        normalized.IsEmpty.Should().BeTrue();
    }

    /// <summary>
    /// The contract offers no free-text field at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A structural test, deliberately. Normalising a route string correctly is possible; keeping
    /// it correct as pages are added for the next three years is not, because the scrubber has to
    /// be updated by whoever adds <c>/resources/{id}/share/{token}</c> and nothing makes them.
    /// Removing the field is the only version of this that stays true.
    /// </para>
    /// <para>
    /// The one string that remains, <c>AppVersion</c>, is shape-validated above and is asserted
    /// here to be the only one.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_metadata_contract_carries_no_free_text_route()
    {
        var stringProperties = typeof(ClientMetadata)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        stringProperties.Should().BeEquivalentTo(
            ["AppVersion"],
            "AppVersion is shape-validated; any other string member would be an unvalidated channel "
            + "into a public issue body");

        typeof(ClientMetadata).GetProperty("CurrentRoute").Should().BeNull(
            "the raw route string is the disclosure this design removed");
    }
}

/// <summary>
/// Route-shaped values, end to end, against a real host.
/// </summary>
public sealed class FeedbackRoutePrivacyPostgresTests : IAsyncLifetime
{
    private const string Owner = "user-feedback-route-privacy";

    private FeedbackPostgresHarness _harness = null!;
    private FeedbackApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (CoachPostgresServer.SkipReason is not null)
        {
            return;
        }

        _harness = await FeedbackPostgresHarness.CreateAsync("routepriv");
        _factory = new FeedbackApiFactory(_harness.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>
    /// A caller sending route-shaped values by hand cannot get them into the issue body.
    /// </summary>
    /// <remarks>
    /// The request goes over the wire as raw JSON so it can carry things the C# contract cannot
    /// express — an out-of-range enum ordinal, a string where an enum belongs, an extra property
    /// with the old field's name. None of it may appear in what is posted.
    /// </remarks>
    [PostgresTheory]
    [InlineData("{\"routeCategory\":4210}")]
    [InlineData("{\"currentRoute\":\"/resources/edit/4821?token=abc123\"}")]
    [InlineData("{\"currentRoute\":\"/diary/2026-08-21\",\"routeCategory\":2}")]
    [InlineData("{\"appVersion\":\"/Users/learner/private.txt\"}")]
    [InlineData("{\"appVersion\":\"learner@example.com\",\"platform\":88}")]
    public async Task Route_shaped_input_never_reaches_the_issue_body(string metadataJson)
    {
        using var client = _factory.CreateClientFor(Owner);

        var body = $$"""
            {"description":"The page did not load.","feedbackType":"bug","clientMetadata":{{metadataJson}}}
            """;

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/feedback/preview", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;

        preview.FormattedBody.Should().NotContain("4821");
        preview.FormattedBody.Should().NotContain("token=abc123");
        preview.FormattedBody.Should().NotContain("2026-08-21");
        preview.FormattedBody.Should().NotContain("/Users/learner");
        preview.FormattedBody.Should().NotContain("learner@example.com");
        preview.FormattedBody.Should().NotContain("4210");
        preview.FormattedBody.Should().NotContain("88");
    }

    /// <summary>
    /// The stored ledger row records closed codes, never a route or a raw version.
    /// </summary>
    [PostgresFact]
    public async Task The_ledger_records_only_closed_codes_for_context()
    {
        using var client = _factory.CreateClientFor(Owner);

        var previewResponse = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = "Something broke.",
            FeedbackType = "bug",
            ClientMetadata = new ClientMetadata
            {
                AppVersion = "/Users/learner/private.txt",
                Platform = (FeedbackPlatform)88,
                RouteCategory = (FeedbackRouteCategory)4210
            }
        });

        var preview = (await previewResponse.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;

        var submit = await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = preview.PreviewToken });
        submit.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var check = _harness.NewContext();
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleAsync(check.FeedbackSubmissions);

        row.RouteCategory.Should().Be(FeedbackRouteCategory.Unknown);
        row.Platform.Should().Be(FeedbackPlatform.Unknown);
        row.AppVersion.Should().Be(FeedbackClientMetadataNormalizer.UnknownVersion);
        Enum.IsDefined(row.RouteCategory).Should().BeTrue();
        Enum.IsDefined(row.Platform).Should().BeTrue();
    }

    /// <summary>
    /// Preview body and issue body carry no learner identifier.
    /// </summary>
    /// <remarks>
    /// The metadata block never carried one, and this is the test that keeps it that way: the
    /// profile id is in the signed payload for ownership, and there is no reason for it to be in
    /// something published.
    /// </remarks>
    [PostgresFact]
    public async Task The_issue_body_never_carries_the_learner_identifier()
    {
        using var client = _factory.CreateClientFor(Owner);

        var previewResponse = await client.PostAsJsonAsync("/api/v1/feedback/preview", new FeedbackRequest
        {
            Description = "Please fix the thing.",
            FeedbackType = "bug",
            ClientMetadata = new ClientMetadata
            {
                AppVersion = "1.2.3",
                Platform = FeedbackPlatform.Web,
                RouteCategory = FeedbackRouteCategory.Dashboard
            }
        });

        var preview = (await previewResponse.Content.ReadFromJsonAsync<FeedbackPreviewResponse>())!;
        preview.FormattedBody.Should().NotContain(Owner);

        await client.PostAsJsonAsync(
            "/api/v1/feedback/submit", new FeedbackSubmitRequest { PreviewToken = preview.PreviewToken });

        _factory.GitHub.Bodies.TryDequeue(out var posted).Should().BeTrue();
        posted.Should().NotContain(Owner);

        using var doc = JsonDocument.Parse(posted!);
        doc.RootElement.GetProperty("body").GetString().Should().NotContain(Owner);
    }
}
