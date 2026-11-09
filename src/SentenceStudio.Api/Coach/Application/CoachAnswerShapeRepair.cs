using SentenceStudio.Contracts.Coach;
using SentenceStudio.Contracts.Coach.Intent;

namespace SentenceStudio.Api.Coach.Application;

/// <summary>
/// Repairs the structural slips a model makes in a pedagogical answer before the answer is
/// validated, so an otherwise good reply is not refused for its packaging.
/// </summary>
/// <remarks>
/// <para>
/// The deployed model is not run in strict structured-output mode. Two slips recur on ordinary
/// questions ("How much did I practice this week?", a follow-up to a plan request):
/// <list type="bullet">
/// <item>an empty placeholder answer — <c>{"Topic":…,"Blocks":[]}</c> — sent on a turn that does
/// not answer anything, or with the reply written into <c>CoachMessage</c> instead;</item>
/// <item>one part per list item (one per day of the week, say), which overruns the part limit.</item>
/// </list>
/// Both used to end the turn with "An answer must have between 1 and 8 parts".
/// </para>
/// <para>
/// The repair never adds model text that the learner would not otherwise see, never cuts text,
/// and never relaxes a bound. Whitespace-only pieces are dropped; an answer with no text left is
/// treated as absent; overflow parts are folded, with their headings kept inline, into one part.
/// The result still goes through the same intent validator, projection bounds, character
/// budgets, and grounding as any other answer, so anything that does not fit is still refused.
/// </para>
/// </remarks>
public static class CoachAnswerShapeRepair
{
    /// <summary>What the repair changed. Counts only; never content.</summary>
    public readonly record struct Report(
        int DroppedEmptySpans,
        int DroppedEmptyBlocks,
        int FoldedBlocks,
        bool DroppedEmptyAnswer,
        bool AnswerFromMessage,
        bool GeneralAnswerWithParts = false)
    {
        public bool Changed =>
            DroppedEmptySpans > 0 || DroppedEmptyBlocks > 0 || FoldedBlocks > 0
            || DroppedEmptyAnswer || AnswerFromMessage || GeneralAnswerWithParts;
    }

    /// <summary>Repairs <paramref name="intent"/>'s answer in place and reports what changed.</summary>
    public static Report Repair(CoachTurnIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var droppedSpans = 0;
        var droppedBlocks = 0;
        var folded = 0;
        var droppedAnswer = false;
        var fromMessage = false;
        var generalWithParts = false;

        // A general answer that also carried structured parts (a word list, say). Both kinds are
        // answers that write nothing, so the reply goes through the answer path, with its bounds
        // and language tags, instead of being refused for its packaging. The answer path shows
        // only the parts, so the message leads them as the direct answer rather than being lost.
        // Done before the part repair below, so the extra part is folded like any other.
        if (intent.Kind == CoachIntentKind.GeneralAnswer && intent.PedagogicalAnswer is { } general)
        {
            intent.Kind = CoachIntentKind.PedagogicalAnswer;
            generalWithParts = true;
            if (!string.IsNullOrWhiteSpace(intent.CoachMessage))
            {
                general.Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = SplitIntoSpans(intent.CoachMessage.Trim())
                            .Select(text => new CoachAnswerSpanIntent { Text = text, Language = CoachLanguageRole.Display })
                            .ToList()
                    },
                    .. general.Blocks ?? []
                ];
            }
        }

        if (intent.PedagogicalAnswer is { } answer)
        {
            var blocks = new List<CoachAnswerBlockIntent>();
            foreach (var block in answer.Blocks ?? [])
            {
                if (block is null)
                {
                    droppedBlocks++;
                    continue;
                }

                var spans = (block.Spans ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Text)).ToList();
                droppedSpans += (block.Spans?.Count ?? 0) - spans.Count;

                if (spans.Count == 0)
                {
                    // A heading with no text under it says nothing on its own.
                    droppedBlocks++;
                    continue;
                }

                blocks.Add(new CoachAnswerBlockIntent { Kind = block.Kind, Label = block.Label, Spans = spans });
            }

            if (blocks.Count == 0)
            {
                // An empty answer carries no text, so treating it as absent can neither hide nor
                // add anything. It lets a plan suggestion or a plain reply proceed instead of
                // being refused for an answer it never gave.
                intent.PedagogicalAnswer = null;
                droppedAnswer = true;
            }
            else
            {
                folded = Fold(blocks);
                intent.PedagogicalAnswer = new CoachPedagogicalAnswerIntent { Topic = answer.Topic, Blocks = blocks };
            }
        }

        // An answer turn whose reply landed in CoachMessage instead of the blocks. The message is
        // model text meant for the learner either way; carrying it as the direct answer puts it
        // through the answer bounds and grounding rather than discarding the reply.
        if (intent.Kind == CoachIntentKind.PedagogicalAnswer
            && intent.PedagogicalAnswer is null
            && !string.IsNullOrWhiteSpace(intent.CoachMessage))
        {
            intent.PedagogicalAnswer = new CoachPedagogicalAnswerIntent
            {
                Topic = CoachAnswerTopic.Other,
                Blocks =
                [
                    new CoachAnswerBlockIntent
                    {
                        Kind = CoachAnswerBlockKind.Answer,
                        Spans = SplitIntoSpans(intent.CoachMessage.Trim())
                            .Select(text => new CoachAnswerSpanIntent { Text = text, Language = CoachLanguageRole.Display })
                            .ToList()
                    }
                ]
            };
            fromMessage = true;
        }

        return new Report(droppedSpans, droppedBlocks, folded, droppedAnswer, fromMessage, generalWithParts);
    }

    /// <summary>
    /// Folds parts beyond <see cref="CoachAnswerLimits.MaxBlocks"/> into the last allowed part,
    /// keeping a trailing retrieval prompt last. Returns how many parts were folded away.
    /// </summary>
    private static int Fold(List<CoachAnswerBlockIntent> blocks)
    {
        if (blocks.Count <= CoachAnswerLimits.MaxBlocks)
        {
            return 0;
        }

        CoachAnswerBlockIntent? prompt = null;
        if (blocks[^1].Kind == CoachAnswerBlockKind.RetrievalPrompt)
        {
            prompt = blocks[^1];
            blocks.RemoveAt(blocks.Count - 1);
        }

        var keep = CoachAnswerLimits.MaxBlocks - (prompt is null ? 1 : 2);
        var overflow = blocks.Skip(keep).ToList();
        blocks.RemoveRange(keep, blocks.Count - keep);

        var kinds = overflow.Select(b => b.Kind).Distinct().ToList();
        var spans = new List<CoachAnswerSpanIntent>();
        foreach (var block in overflow)
        {
            var label = block.Label?.Trim();
            for (var i = 0; i < block.Spans.Count; i++)
            {
                var span = block.Spans[i];
                spans.Add(new CoachAnswerSpanIntent
                {
                    // The heading stays with its text, so no words are lost in the fold.
                    Text = i == 0 && !string.IsNullOrEmpty(label) ? $"{label}: {span.Text.Trim()}" : span.Text.Trim(),
                    Language = span.Language
                });
            }
        }

        blocks.Add(new CoachAnswerBlockIntent
        {
            Kind = kinds.Count == 1 ? kinds[0] : CoachAnswerBlockKind.Note,
            Spans = Coalesce(spans)
        });

        if (prompt is not null)
        {
            blocks.Add(prompt);
        }

        return overflow.Count - 1;
    }

    /// <summary>
    /// Joins neighbouring pieces in the same language while there are more than a part allows,
    /// without making any piece longer than <see cref="CoachAnswerLimits.MaxSpanCharacters"/>.
    /// What still does not fit is left for the projection to refuse.
    /// </summary>
    private static List<CoachAnswerSpanIntent> Coalesce(List<CoachAnswerSpanIntent> spans)
    {
        var result = new List<CoachAnswerSpanIntent>(spans);
        var merged = true;
        while (result.Count > CoachAnswerLimits.MaxSpansPerBlock && merged)
        {
            merged = false;
            var best = -1;
            for (var i = 0; i + 1 < result.Count; i++)
            {
                if (result[i].Language != result[i + 1].Language)
                {
                    continue;
                }

                var length = result[i].Text.Length + 1 + result[i + 1].Text.Length;
                if (length <= CoachAnswerLimits.MaxSpanCharacters
                    && (best < 0 || length < result[best].Text.Length + 1 + result[best + 1].Text.Length))
                {
                    best = i;
                }
            }

            if (best >= 0)
            {
                result[best] = new CoachAnswerSpanIntent
                {
                    Text = $"{result[best].Text} {result[best + 1].Text}",
                    Language = result[best].Language
                };
                result.RemoveAt(best + 1);
                merged = true;
            }
        }

        return result;
    }

    /// <summary>Splits text at sentence ends into pieces that fit one span.</summary>
    private static List<string> SplitIntoSpans(string text)
    {
        if (text.Length <= CoachAnswerLimits.MaxSpanCharacters)
        {
            return [text];
        }

        var pieces = new List<string>();
        var current = new System.Text.StringBuilder();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var end = i == text.Length - 1
                || (text[i] is '.' or '?' or '!' && char.IsWhiteSpace(text[i + 1]));
            if (!end)
            {
                continue;
            }

            var sentence = text[start..(i + 1)].Trim();
            start = i + 1;
            if (current.Length > 0 && current.Length + 1 + sentence.Length > CoachAnswerLimits.MaxSpanCharacters)
            {
                pieces.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString());
        }

        // A single sentence longer than a span is left whole for the projection to refuse.
        return pieces;
    }
}
