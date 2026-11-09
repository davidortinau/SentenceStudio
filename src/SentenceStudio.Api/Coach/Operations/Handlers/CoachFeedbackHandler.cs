using System.ComponentModel;
using System.Text;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Api.Feedback;
using SentenceStudio.Api.Feedback.Persistence;
using SentenceStudio.Contracts.Feedback;

namespace SentenceStudio.Api.Coach.Operations.Handlers;

/// <summary>What the model supplies for <c>propose_feedback</c>. Domain fields only.</summary>
public sealed record CoachFeedbackArgs(
    [property: Description("bug for something broken or wrong, enhancement for a change or new feature the learner wants.")]
    string Kind,
    [property: Description("A short issue title in the learner's words, at most 80 characters.")]
    string Title,
    [property: Description("What the learner reported, in their own words: what is wrong and what they want instead. Do not add details they did not give.")]
    string Description);

/// <summary>
/// The stored form. Same domain fields as <see cref="CoachFeedbackArgs"/>, plus server-owned
/// fields filled at preparation from the turn — never from the model.
/// </summary>
public sealed record CoachFeedbackStoredArgs(
    string Kind,
    string Title,
    string Description,
    string? Jti = null,
    string? Reference = null,
    FeedbackScreenContextNormalizer.NormalizedScreenContext? Screen = null);

/// <summary>
/// Files a learner's bug report or request as a public GitHub issue, with the screen they were on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hard, confirmed, irreversible.</b> The issue is public the moment it exists, so the learner
/// sees the exact title and the screen summary before anything is sent, and there is no undo.
/// </para>
/// <para>
/// <b>Screen context comes from the turn, not the model.</b> Preparation reads the client's screen
/// context from <see cref="CoachWriteTurnScope"/>, splits it into a public part (identifiers only)
/// and a private part, and stores both in the canonical arguments. Execution writes the private
/// part to <see cref="FeedbackScreenSnapshot"/> and puts only its reference in the issue.
/// </para>
/// <para>
/// <b>Exactly once.</b> The preview's nonce becomes the feedback ledger key, so an approval that is
/// re-run replays the first issue instead of filing a second.
/// </para>
/// </remarks>
public sealed class CoachFeedbackHandler : CoachWriteHandlerBase<CoachFeedbackStoredArgs>
{
    public const int TitleMaxLength = FeedbackPreviewToken.MaxTitleLength;
    /// <summary>Upper bound on the stored description; the card budget below may trim it further.</summary>
    public const int DescriptionMaxLength = 1200;
    private const int CardLineLength = 400;

    /// <summary>
    /// Card lines the description may use. The card holds 12 lines: type, title, the public
    /// warning, up to four screen lines and the private reference take 8, so the description
    /// gets 4. The description is trimmed to fit, so nothing published is ever cut off the card.
    /// </summary>
    private const int DetailLines = 4;

    private readonly CoachWriteTurnScope _turn;
    private readonly IFeedbackIssueFiler _filer;
    private readonly FeedbackDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<CoachFeedbackHandler> _logger;

    public CoachFeedbackHandler(
        CoachWriteTurnScope turn,
        IFeedbackIssueFiler filer,
        FeedbackDbContext db,
        TimeProvider time,
        ILogger<CoachFeedbackHandler> logger)
    {
        _turn = turn;
        _filer = filer;
        _db = db;
        _time = time;
        _logger = logger;
    }

    public override string ToolName => CoachToolNames.ProposeFeedback;
    public override CoachToolRiskClass RiskClass => CoachToolRiskClass.WriteHard;
    public override CoachWriteEntityKind EntityKind => CoachWriteEntityKind.FeedbackReport;

    protected override Task<CoachWritePreview> PrepareAsync(
        string userProfileId, CoachFeedbackStoredArgs args, CancellationToken cancellationToken)
    {
        var (type, title, description) = Validate(args);

        var screen = FeedbackScreenContextNormalizer.Normalize(_turn.ClientScreen);
        var reference = screen.IsEmpty ? null : NewReference();
        var stored = new CoachFeedbackStoredArgs(
            type, title, description, FeedbackPreviewToken.NewJti(), reference, screen.IsEmpty ? null : screen);

        // The card shows everything the issue will publish: the whole description and the exact
        // screen lines. Nothing published is left off the card.
        var lines = new List<string>
        {
            type == FeedbackLabels.Bug ? "Type: bug report" : "Type: feature request",
            $"Title: {title}",
            // Near the top, so the consequence is the first thing read after what is being filed.
            $"This creates a public GitHub issue on {FeedbackIssueFiler.GitHubRepo} and cannot be undone.",
        };
        lines.AddRange(Chunk(DetailsLine(description), CardLineLength));
        if (screen.IsEmpty)
        {
            lines.Add("Screen: not reported by this app version");
        }
        else
        {
            lines.AddRange(screen.PublicLines());
        }
        if (reference is not null)
        {
            lines.Add($"Private screen state will be saved under reference {reference}; it is not published.");
        }

        return Task.FromResult(new CoachWritePreview(
            type == FeedbackLabels.Bug ? "File a bug report on GitHub" : "File a feature request on GitHub",
            lines,
            EntityId: null,
            Canonical(stored)));
    }

    protected override async Task<CoachWriteExecution> ExecuteAsync(
        string userProfileId, CoachFeedbackStoredArgs args, CancellationToken cancellationToken)
    {
        var (type, title, description) = Validate(args);
        if (string.IsNullOrWhiteSpace(args.Jti) || args.Jti.Length > FeedbackPreviewToken.MaxJtiLength)
        {
            throw InvalidArgument("This feedback proposal is from an older version. Ask Sam to file it again.");
        }

        var screen = args.Screen ?? FeedbackScreenContextNormalizer.Normalize(null);
        var body = ComposeBody(type, description, screen, args.Reference);
        var now = _time.GetUtcNow();

        if (args.Reference is not null)
        {
            await SaveSnapshotAsync(userProfileId, args.Jti, args.Reference, screen, now.UtcDateTime, cancellationToken)
                .ConfigureAwait(false);
        }

        var payload = new FeedbackPreviewPayload(
            args.Jti,
            title,
            body,
            FeedbackLabels.Sanitize([type], type),
            type,
            userProfileId,
            FeedbackRouteCategory.Coach,
            screen.Platform == "web" ? FeedbackPlatform.Web : FeedbackPlatform.Native,
            FeedbackClientMetadataNormalizer.NormalizeVersion(screen.AppVersion?.Split(' ')[0]),
            now.ToUnixTimeSeconds(),
            now.AddMinutes(10).ToUnixTimeSeconds());

        FeedbackFileOutcome outcome;
        try
        {
            outcome = await _filer.FileAsync(payload, userProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw DataAccessFailure(ex);
        }

        if (!outcome.Filed && outcome.Kind != FeedbackFileOutcomeKind.SettlementFailed)
        {
            // Nothing exists in public for these, so the private state has nothing to belong to.
            // In-doubt outcomes keep it: an issue may exist and may name the reference.
            if (args.Reference is not null && outcome.Kind is FeedbackFileOutcomeKind.RateLimited
                    or FeedbackFileOutcomeKind.Unconfigured or FeedbackFileOutcomeKind.GitHubRefused
                    or FeedbackFileOutcomeKind.GitHubUnavailable or FeedbackFileOutcomeKind.Closed)
            {
                await DeleteSnapshotAsync(userProfileId, args.Jti, cancellationToken).ConfigureAwait(false);
            }

            throw outcome.Kind switch
            {
                FeedbackFileOutcomeKind.RateLimited => InvalidArgument(
                    $"You have reached the feedback limit. Nothing was filed. In {outcome.Rate?.RetryAfterSeconds ?? 60} second(s), ask Sam to file it again."),
                FeedbackFileOutcomeKind.Unconfigured => InvalidArgument(
                    "Feedback filing is not set up on this server. Nothing was filed."),
                FeedbackFileOutcomeKind.GitHubRefused => InvalidArgument(
                    "GitHub did not accept the report. Nothing was filed."),
                FeedbackFileOutcomeKind.GitHubUnavailable => InvalidArgument(
                    "GitHub could not be reached, so nothing was filed. Ask Sam to file it again in a few minutes."),
                FeedbackFileOutcomeKind.Closed => InvalidArgument(
                    "This report was not filed and can no longer be sent. Ask Sam to file it again."),
                _ => InvalidArgument(
                    "The report may have reached GitHub. Check the repository before filing it again.")
            };
        }

        // An issue exists but its identity was not recorded: report it as filed, without a link
        // we do not have, so the learner is not prompted to file a duplicate.
        if (outcome.IssueNumber <= 0 || string.IsNullOrWhiteSpace(outcome.IssueUrl))
        {
            var unlinked = new List<string>
            {
                $"The link could not be recorded. Search {FeedbackIssueFiler.GitHubRepo} issues for \u201c{title}\u201d."
            };
            if (args.Reference is not null)
            {
                unlinked.Add($"Private screen state: {args.Reference}");
            }

            return new CoachWriteExecution("Filed the issue on GitHub", unlinked, EntityId: null, PriorStateJson: null);
        }

        var resultLines = new List<string> { $"Issue #{outcome.IssueNumber}: {outcome.IssueUrl}" };
        if (args.Reference is not null)
        {
            resultLines.Add($"Private screen state: {args.Reference}");
        }

        return new CoachWriteExecution(
            $"Filed issue #{outcome.IssueNumber}",
            resultLines,
            outcome.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PriorStateJson: null);
    }

    /// <summary>The public issue body. Deterministic, so the filed body matches what was previewed.</summary>
    public static string ComposeBody(
        string type,
        string description,
        FeedbackScreenContextNormalizer.NormalizedScreenContext screen,
        string? reference)
    {
        var sb = new StringBuilder();
        sb.AppendLine(type == FeedbackLabels.Bug ? "## Bug report" : "## Feature request");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.Append(screen.PublicMarkdown(reference));
        sb.AppendLine();
        sb.AppendLine("_Filed through Sam in SentenceStudio._");
        return sb.ToString();
    }

    private async Task DeleteSnapshotAsync(string userProfileId, string jti, CancellationToken cancellationToken)
    {
        try
        {
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(
                    _db.FeedbackScreenSnapshots.Where(s => s.Jti == jti && s.UserProfileId == userProfileId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cleanup failure must not hide the real outcome; retention removes the row later.
            _logger.LogWarning(ex, "A feedback screen snapshot could not be removed after a report was not filed.");
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    private async Task SaveSnapshotAsync(
        string userProfileId,
        string jti,
        string reference,
        FeedbackScreenContextNormalizer.NormalizedScreenContext screen,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // A re-run of the same approval finds its own row and moves on.
        var exists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
            _db.FeedbackScreenSnapshots, s => s.Jti == jti && s.UserProfileId == userProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        // The normaliser keeps this under its budget by dropping whole entries, so it always parses.
        var json = screen.ToPrivateJson();

        _db.FeedbackScreenSnapshots.Add(new FeedbackScreenSnapshot
        {
            Reference = reference,
            UserProfileId = userProfileId,
            Jti = jti,
            ContextJson = json,
            CreatedAtUtc = now
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DataAccessFailure(ex);
        }
    }

    private (string Type, string Title, string Description) Validate(CoachFeedbackStoredArgs args)
    {
        var kind = Clean(args.Kind, 20).ToLowerInvariant();
        var type = kind switch
        {
            "bug" => FeedbackLabels.Bug,
            "enhancement" or "request" or "feature" => FeedbackLabels.Enhancement,
            _ => throw InvalidArgument("Kind must be bug or enhancement.")
        };

        var title = Clean(args.Title, TitleMaxLength);
        if (title.Length == 0)
        {
            throw InvalidArgument("A title is required.");
        }

        var description = FitCard(CleanMultiline(args.Description, DescriptionMaxLength));
        if (description.Length == 0)
        {
            throw InvalidArgument("A description is required.");
        }

        return (type, title, description);
    }

    private static string DetailsLine(string description) => "Details: " + description.Replace("\n", " / ");

    /// <summary>Trims the description until its card line fits the detail budget.</summary>
    private static string FitCard(string description)
    {
        const int budget = DetailLines * CardLineLength;
        while (description.Length > 0 && DetailsLine(description).Length > budget)
        {
            description = description[..^Math.Max(1, DetailsLine(description).Length - budget)].TrimEnd();
        }

        return description;
    }

    /// <summary>Like <c>Clean</c>, but keeps line breaks so steps and paragraphs survive.</summary>
    private static string CleanMultiline(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => new string(line.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).TrimEnd());
        var text = string.Join('\n', lines).Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string NewReference() =>
        "fs-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}
