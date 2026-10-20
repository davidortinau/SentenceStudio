using System.Collections.Immutable;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Adapters;

public sealed class NativeQuizChoicesAdapter(
    IUserScopeProvider scope,
    IPreferencesService preferences,
    LearningResourceRepository resources) : INativeQuizChoices
{
    public async Task<ImmutableArray<NativeQuizChoice>> CreateAsync(
        string userId, VocabularyWord question, IReadOnlyList<VocabularyWord> reachableWords,
        bool promptUsesNativeLanguage, CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(reachableWords);
        await NativeQuizOwnerGuard.RequireOwnerReachableWordsAsync(
            userId, reachableWords.Append(question).ToArray(),
            scope, preferences, resources, cancellationToken);

        var options = VocabQuizSourcePolicy.CreateMultipleChoiceOptions(
            question, reachableWords, promptUsesNativeLanguage);
        if (options.Count is < 2 or > 4
            || options.Select(option => option.Text.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
        {
            throw new NotSupportedException(
                "Multiple choice requires two to four distinct owner-reachable answers.");
        }

        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        return options.Select(option => new NativeQuizChoice(option.Text)
        {
            AudioLanguage = option.Word.Language
        }).ToImmutableArray();
    }
}
