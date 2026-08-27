using System.Text.RegularExpressions;
using FluentAssertions;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Tests.Feedback;

/// <summary>
/// The stored ordinals, pinned.
/// </summary>
/// <remarks>
/// These enums are persisted as integers, so their values are part of the database schema in every
/// way that matters. Re-ordering or inserting a member silently re-labels rows already written —
/// which here means a <c>Failed</c> row becoming a <c>Submitted</c> one, and a token that filed
/// nothing suddenly replaying a receipt for an issue that does not exist.
/// </remarks>
public sealed class FeedbackStoredEnumContractTests
{
    [Theory]
    [InlineData(FeedbackSubmissionStatus.Claimed, 0)]
    [InlineData(FeedbackSubmissionStatus.Submitted, 1)]
    [InlineData(FeedbackSubmissionStatus.Failed, 2)]
    [InlineData(FeedbackSubmissionStatus.Committed, 3)]
    public void Submission_status_ordinals_are_pinned(FeedbackSubmissionStatus status, int expected)
    {
        ((int)status).Should().Be(expected);
    }

    [Theory]
    [InlineData(FeedbackRateKind.Preview, 0)]
    [InlineData(FeedbackRateKind.Submit, 1)]
    public void Rate_kind_ordinals_are_pinned(FeedbackRateKind kind, int expected)
    {
        ((int)kind).Should().Be(expected);
    }

    [Theory]
    [InlineData(FeedbackRouteCategory.Unknown, 0)]
    [InlineData(FeedbackRouteCategory.Dashboard, 1)]
    [InlineData(FeedbackRouteCategory.Activity, 2)]
    [InlineData(FeedbackRouteCategory.Resources, 3)]
    [InlineData(FeedbackRouteCategory.Skills, 4)]
    [InlineData(FeedbackRouteCategory.Profile, 5)]
    [InlineData(FeedbackRouteCategory.Account, 6)]
    [InlineData(FeedbackRouteCategory.Coach, 7)]
    [InlineData(FeedbackRouteCategory.Progress, 8)]
    [InlineData(FeedbackRouteCategory.Feedback, 9)]
    [InlineData(FeedbackRouteCategory.Home, 10)]
    public void Route_category_ordinals_are_pinned(FeedbackRouteCategory category, int expected)
    {
        ((int)category).Should().Be(expected);
    }

    [Theory]
    [InlineData(FeedbackPlatform.Unknown, 0)]
    [InlineData(FeedbackPlatform.Web, 1)]
    [InlineData(FeedbackPlatform.Native, 2)]
    public void Platform_ordinals_are_pinned(FeedbackPlatform platform, int expected)
    {
        ((int)platform).Should().Be(expected);
    }

    /// <summary>
    /// Unknown is zero in both wire enums, so an absent value is the safe value.
    /// </summary>
    /// <remarks>
    /// A JSON body that omits the member deserialises to <c>default</c>. If <c>Unknown</c> were
    /// anything but zero, "the client said nothing" would be indistinguishable from "the client
    /// said <c>Dashboard</c>" — a small lie, but one nobody would ever notice.
    /// </remarks>
    [Fact]
    public void Unknown_is_the_default_for_both_wire_enums()
    {
        default(FeedbackRouteCategory).Should().Be(FeedbackRouteCategory.Unknown);
        default(FeedbackPlatform).Should().Be(FeedbackPlatform.Unknown);
    }

    /// <summary>
    /// The server's failure codes and the wire codes the client branches on are the same strings.
    /// </summary>
    /// <remarks>
    /// They are declared in two assemblies — the codes the client reads live in contracts, the
    /// operator-facing set lives with the endpoint — and the server ones alias the wire ones so
    /// there is one source of truth. Because <c>const</c> is inlined at compile time, an alias that
    /// was later replaced by a literal would still compile and would drift silently; this is what
    /// notices.
    /// </remarks>
    [Fact]
    public void The_server_failure_codes_match_the_wire_codes()
    {
        FeedbackFailureCodes.SubmissionClosed.Should().Be(FeedbackProblemCodes.SubmissionClosed);
        FeedbackFailureCodes.SubmissionInDoubt.Should().Be(FeedbackProblemCodes.SubmissionInDoubt);
        FeedbackFailureCodes.RateLimited.Should().Be(FeedbackProblemCodes.RateLimited);
    }

    /// <summary>The two 409 codes are distinct, which is the only reason either is useful.</summary>
    [Fact]
    public void The_closed_and_in_doubt_codes_are_different_strings()
    {
        FeedbackProblemCodes.SubmissionClosed.Should()
            .NotBe(FeedbackProblemCodes.SubmissionInDoubt);
    }

    /// <summary>
    /// Claimed is zero, so a row created without an explicit status is in doubt.
    /// </summary>
    /// <remarks>
    /// The safest possible default for this table: a row that somehow reached the database without
    /// its status being set refuses every retry rather than looking settled or reusable.
    /// </remarks>
    [Fact]
    public void The_default_submission_status_is_the_one_that_refuses_everything()
    {
        default(FeedbackSubmissionStatus).Should().Be(FeedbackSubmissionStatus.Claimed);
        FeedbackSubmissionStates.IsInDoubt(default).Should().BeTrue();
        FeedbackSubmissionStates.HasReceipt(default).Should().BeFalse();
        FeedbackSubmissionStates.PermitsExternalCall(default).Should().BeFalse();
    }
}

/// <summary>
/// The status classification, including the statuses that do not exist yet.
/// </summary>
/// <remarks>
/// <para>
/// The predicates in <see cref="FeedbackSubmissionStates"/> are written as explicit switches rather
/// than as each other's negation, precisely so that a member added later falls out of all of them
/// and these tests fail. Defining one as <c>!</c> another classifies a new member automatically,
/// and an automatic classification here is a duplicate public issue.
/// </para>
/// <para>
/// The undeclared-ordinal cases are a mutation test in miniature: they simulate the member somebody
/// adds without touching the classifier.
/// </para>
/// </remarks>
public sealed class FeedbackSubmissionStateClassificationTests
{
    public static TheoryData<FeedbackSubmissionStatus> AllDeclared()
    {
        var data = new TheoryData<FeedbackSubmissionStatus>();
        foreach (var status in Enum.GetValues<FeedbackSubmissionStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    /// <summary>Every declared status lands in exactly one classification.</summary>
    [Theory]
    [MemberData(nameof(AllDeclared))]
    public void Every_declared_status_is_classified_exactly_once(FeedbackSubmissionStatus status)
    {
        var matches = new[]
        {
            FeedbackSubmissionStates.HasReceipt(status),
            FeedbackSubmissionStates.IsInDoubt(status),
            FeedbackSubmissionStates.IsClosedWithoutIssue(status)
        }.Count(m => m);

        matches.Should().Be(
            1,
            "a status in two classifications is ambiguous and a status in none is unhandled — both "
            + "are how a second issue gets filed");
    }

    /// <summary>No status, declared or not, permits an external call.</summary>
    [Theory]
    [MemberData(nameof(AllDeclared))]
    public void No_declared_status_permits_an_external_call(FeedbackSubmissionStatus status)
    {
        FeedbackSubmissionStates.PermitsExternalCall(status).Should().BeFalse(
            "an existing row always answers for its token; only the absence of a row permits a call");
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void An_unclassified_status_falls_into_no_classification_and_permits_nothing(int ordinal)
    {
        var status = (FeedbackSubmissionStatus)ordinal;

        FeedbackSubmissionStates.HasReceipt(status).Should().BeFalse();
        FeedbackSubmissionStates.IsClosedWithoutIssue(status).Should().BeFalse();
        FeedbackSubmissionStates.PermitsExternalCall(status).Should().BeFalse();

        // In-doubt is deliberately the arm an unknown status does NOT reach either: it is defined
        // by an explicit switch too. The endpoint's own default arm is what catches it, and the
        // ledger's Classify test below proves that.
        FeedbackSubmissionStates.IsInDoubt(status).Should().BeFalse();
    }
}

/// <summary>
/// What the feedback lane is allowed to write to a log.
/// </summary>
/// <remarks>
/// <para>
/// Source-level, because there is no runtime surface that reports "somebody logged an identifier".
/// The scan reads every logging call in the feedback lane and fails on the specific shapes that
/// carry learner data into an operator log.
/// </para>
/// <para>
/// The owner-mismatch case is the one worth stating out loud. The obvious log line names both
/// profile ids so an operator can see who did it; what it produces is a durable, searchable record
/// linking two accounts, written on a path any caller can trigger by replaying a token they found.
/// </para>
/// </remarks>
public sealed class FeedbackLoggingPrivacyTests
{
    private static readonly string[] BannedPlaceholders =
    [
        "{UserProfileId}", "{Caller}", "{TokenOwner}", "{Owner}", "{OwnerProfileId}",
        "{Description}", "{Title}", "{Body}", "{IssueTitle}", "{IssueBody}",
        "{Key}", "{HmacKey}", "{SigningKey}", "{Pat}", "{Token}", "{PreviewToken}",
        "{ErrorBody}", "{ResponseBody}", "{Email}"
    ];

    private static IEnumerable<(string Path, string Source)> FeedbackSources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();

        var api = Path.Combine(root!.FullName, "src", "SentenceStudio.Api");
        var files = Directory
            .EnumerateFiles(Path.Combine(api, "Feedback"), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(api, "FeedbackEndpoints.cs"));

        foreach (var file in files)
        {
            yield return (Path.GetRelativePath(root.FullName, file), File.ReadAllText(file));
        }
    }

    /// <summary>No logging template in the feedback lane names a learner or a secret.</summary>
    [Fact]
    public void No_feedback_log_statement_carries_an_identifier_or_a_secret()
    {
        var offenders = new List<string>();

        foreach (var (path, source) in FeedbackSources())
        {
            foreach (Match match in Regex.Matches(
                         source, @"Log(?:Information|Warning|Error|Critical|Debug|Trace)\s*\((.*?)\);",
                         RegexOptions.Singleline))
            {
                foreach (var banned in BannedPlaceholders)
                {
                    if (match.Groups[1].Value.Contains(banned, StringComparison.Ordinal))
                    {
                        offenders.Add($"{path}: {banned}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "feedback logs carry closed codes and counts only — an identifier here is a durable "
            + "record of who submitted what, written on a path any caller can trigger");
    }

    /// <summary>
    /// The GitHub error body is never logged.
    /// </summary>
    /// <remarks>
    /// GitHub echoes the submitted title and body back in a validation error, so logging the
    /// response would copy learner text into operator logs on exactly the path where something has
    /// already gone wrong — the path most likely to be pasted into a ticket.
    /// </remarks>
    [Fact]
    public void The_github_response_body_is_never_read_into_a_log()
    {
        foreach (var (path, source) in FeedbackSources())
        {
            source.Should().NotContain(
                "ReadAsStringAsync",
                $"{path} must not materialise the GitHub response body; it echoes the submitted "
                + "title and body");
        }
    }

    /// <summary>Neither signing key is interpolated anywhere in the feedback lane.</summary>
    [Fact]
    public void Neither_signing_key_is_interpolated_into_any_string()
    {
        foreach (var (path, source) in FeedbackSources())
        {
            source.Should().NotContain("{signingKey}", $"{path} must never render a key");
            source.Should().NotContain("{configured}", $"{path} must never render a key");
            source.Should().NotContain("{jwtKey}", $"{path} must never render a key");
        }
    }

    /// <summary>Every failure code the lane logs is a declared constant.</summary>
    /// <remarks>
    /// The value of a closed code set is that an operator can enumerate the reasons a request can
    /// fail without reading the code. An ad-hoc string in one log line breaks that quietly.
    /// </remarks>
    [Fact]
    public void Every_logged_failure_code_comes_from_the_closed_set()
    {
        var declared = typeof(FeedbackFailureCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        declared.Should().NotBeEmpty();

        foreach (var (path, source) in FeedbackSources())
        {
            foreach (Match match in Regex.Matches(source, @"Code=\{FailureCode\}"))
            {
                match.Success.Should().BeTrue($"{path} uses the structured placeholder");
            }

            // A literal code=... string would bypass the constants entirely.
            Regex.IsMatch(source, @"Code=[a-z_]+""").Should().BeFalse(
                $"{path} must pass a declared constant rather than a literal code");
        }
    }
}
