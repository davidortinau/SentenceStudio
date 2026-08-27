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
