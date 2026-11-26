using System.Text;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Validates a model-produced answer against its bounds and turns it into the client contract.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs <b>before</b> the answer is scanned for leaks, shown, or persisted. A
/// malformed or oversized answer is refused outright rather than truncated: silently trimming an
/// explanation can cut it mid-sentence and change what it says, which for a teaching answer is
/// worse than declining to show one.
/// </para>
/// <para>
/// The flattened <see cref="CoachAnswerDto.PlainText"/> is derived here from the same spans, so
/// a client that cannot render blocks sees exactly the text that was validated and scanned —
/// never a second, unchecked copy.
/// </para>
/// </remarks>
public sealed class CoachAnswerProjection
{
    /// <summary>The outcome of validating one answer.</summary>
    public sealed record Result(CoachAnswerDto? Answer, IReadOnlyList<string> Errors)
    {
        public bool IsValid => Errors.Count == 0 && Answer is not null;
    }

    /// <summary>Validates and projects, or explains why it will not.</summary>
    public Result Project(CoachPedagogicalAnswerIntent? intent, CoachLanguageProfile languages)
    {
        ArgumentNullException.ThrowIfNull(languages);

        var errors = new List<string>();

        if (intent is null)
        {
            return new Result(null, ["The turn claims to answer a question but carries no answer."]);
        }

        if (!Enum.IsDefined(intent.Topic))
        {
            errors.Add("The answer topic is not one of the allowed topics.");
        }

        var blocks = intent.Blocks ?? [];
        if (blocks.Count < CoachAnswerLimits.MinBlocks || blocks.Count > CoachAnswerLimits.MaxBlocks)
        {
            errors.Add(
                $"An answer must have between {CoachAnswerLimits.MinBlocks} and {CoachAnswerLimits.MaxBlocks} parts.");
            return new Result(null, errors);
        }

        // The direct answer comes first, and only once. A client renders the first block as the
        // headline, so an answer that buries it reads as an aside.
        if (blocks[0].Kind != CoachAnswerBlockKind.Answer)
        {
            errors.Add("The first part of an answer must be the direct answer.");
        }

        if (blocks.Count(b => b.Kind == CoachAnswerBlockKind.Answer) > 1)
        {
            errors.Add("An answer may hold only one direct answer.");
        }

        var prompts = blocks.Count(b => b.Kind == CoachAnswerBlockKind.RetrievalPrompt);
        if (prompts > CoachAnswerLimits.MaxRetrievalPrompts)
        {
            errors.Add($"An answer may hold at most {CoachAnswerLimits.MaxRetrievalPrompts} retrieval prompt.");
        }

        if (prompts == 1 && blocks[^1].Kind != CoachAnswerBlockKind.RetrievalPrompt)
        {
            errors.Add("A retrieval prompt must be the last part of an answer.");
        }

        var projected = new List<CoachAnswerBlockDto>(blocks.Count);
        var total = 0;

        foreach (var block in blocks)
        {
            if (!Enum.IsDefined(block.Kind))
            {
                errors.Add("An answer part has a kind that is not allowed.");
                continue;
            }

            var label = Sanitize(block.Label);
            if (label is { Length: > CoachAnswerLimits.MaxBlockLabelCharacters })
            {
                errors.Add($"A part heading is longer than {CoachAnswerLimits.MaxBlockLabelCharacters} characters.");
                continue;
            }

            var spans = block.Spans ?? [];
            if (spans.Count < CoachAnswerLimits.MinSpansPerBlock || spans.Count > CoachAnswerLimits.MaxSpansPerBlock)
            {
                errors.Add(
                    $"Each part must hold between {CoachAnswerLimits.MinSpansPerBlock} and " +
                    $"{CoachAnswerLimits.MaxSpansPerBlock} pieces of text.");
                continue;
            }

            var projectedSpans = new List<CoachAnswerSpanDto>(spans.Count);
            foreach (var span in spans)
            {
                var text = Sanitize(span.Text);
                if (string.IsNullOrEmpty(text))
                {
                    errors.Add("A piece of text in the answer is empty.");
                    continue;
                }

                text = WithoutFieldNames(text, block.Kind);
                if (text.Length == 0)
                {
                    // The piece was only a field name ("Target:"). It says nothing, so it is
                    // left out; refusing the whole answer over it would cost the learner the rest.
                    continue;
                }

                if (text.Length > CoachAnswerLimits.MaxSpanCharacters)
                {
                    errors.Add($"A piece of text is longer than {CoachAnswerLimits.MaxSpanCharacters} characters.");
                    continue;
                }

                if (!Enum.IsDefined(span.Language))
                {
                    errors.Add("A piece of text names a language role that is not allowed.");
                    continue;
                }

                total += text.Length;
                projectedSpans.Add(new CoachAnswerSpanDto
                {
                    Text = text,
                    Language = span.Language,
                    // Server-resolved. The model chose a role, never a locale.
                    LanguageTag = languages.Tag(span.Language)
                });
            }

            if (projectedSpans.Count == 0)
            {
                continue;
            }

            projected.Add(new CoachAnswerBlockDto
            {
                Kind = block.Kind,
                Label = string.IsNullOrEmpty(label) ? null : label,
                Spans = projectedSpans
            });
        }

        if (total > CoachAnswerLimits.MaxTotalCharacters)
        {
            errors.Add($"The answer is longer than {CoachAnswerLimits.MaxTotalCharacters} characters.");
        }

        if (projected.Count == 0)
        {
            errors.Add("The answer has no usable text.");
        }

        if (errors.Count > 0)
        {
            return new Result(null, errors);
        }

        var plainText = Flatten(projected);
        if (plainText.Length > CoachAnswerLimits.MaxFallbackCharacters)
        {
            return new Result(null, ["The flattened answer is longer than the allowed length."]);
        }

        return new Result(
            new CoachAnswerDto
            {
                Topic = intent.Topic,
                Blocks = projected,
                PlainText = plainText,
                TargetLanguageTag = languages.TargetLanguageTag,
                DisplayLanguageTag = languages.DisplayLanguageTag,
                EndsWithRecallQuestion = projected.Any(b => b.Kind == CoachAnswerBlockKind.RetrievalPrompt)
            },
            Array.Empty<string>());
    }

    /// <summary>Every piece of text in an answer, for leak scanning.</summary>
    public static IEnumerable<string> TextsToScan(CoachAnswerDto answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        foreach (var block in answer.Blocks)
        {
            if (!string.IsNullOrEmpty(block.Label))
            {
                yield return block.Label;
            }

            foreach (var span in block.Spans)
            {
                yield return span.Text;
            }
        }

        // The fallback is derived from the spans above, but it is scanned as well: a future
        // change to how it is built must not become a way to ship unscanned text.
        yield return answer.PlainText;
    }

    private static string Flatten(IReadOnlyList<CoachAnswerBlockDto> blocks)
    {
        var builder = new StringBuilder();

        foreach (var block in blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            if (!string.IsNullOrEmpty(block.Label))
            {
                builder.Append(block.Label).Append(": ");
            }

            for (var i = 0; i < block.Spans.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(block.Spans[i].Text);
            }
        }

        return builder.ToString();
    }

    private static readonly string[] LanguageRoleNames = Enum.GetNames<CoachLanguageRole>();

    /// <summary>
    /// Removes the names the model sometimes writes at the start of a piece of text: the value of
    /// its language field ("Target:", "Display:", "Native:") and the kind of its part ("Note:").
    /// </summary>
    /// <remarks>
    /// Those names belong to the fields beside the text. A learner reads the text as Sam's own
    /// words, and every client already shows the part's heading, so "Target: 밥 먹었어?" and
    /// "Display: Note: ..." read as debris (issue 272). Only a name followed by a colon at the
    /// very start is removed; the same word inside a sentence, or without the colon, is kept.
    /// </remarks>
    private static string WithoutFieldNames(string text, CoachAnswerBlockKind kind)
    {
        var kindName = kind.ToString();
        var rest = text.AsSpan();

        while (true)
        {
            var after = AfterName(rest, kindName);
            foreach (var role in LanguageRoleNames)
            {
                if (after >= 0)
                {
                    break;
                }

                after = AfterName(rest, role);
            }

            if (after < 0)
            {
                break;
            }

            rest = rest[after..].TrimStart();
        }

        return rest.Length == text.Length ? text : rest.ToString();
    }

    /// <summary>The index just past "<paramref name="name"/>:" at the start of the text, or -1.</summary>
    private static int AfterName(ReadOnlySpan<char> text, string name)
    {
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var rest = text[name.Length..];
        var colon = rest.TrimStart();
        return colon.Length > 0 && colon[0] is ':' or '\uFF1A'
            ? text.Length - colon.Length + 1
            : -1;
    }

    /// <summary>
    /// Collapses control characters and trims. Answers are plain text: no markup passes through,
    /// so nothing here can become markup in a client that renders it.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) && c != '\n' ? ' ' : c);
        }

        return builder.ToString().Replace('\n', ' ').Trim();
    }
}
