using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Adapters;

internal static class NativeQuizOwnerGuard
{
    internal static void RequireActiveOwner(
        string userId, IUserScopeProvider scope, IPreferencesService preferences,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userId)
            || !scope.TryGetUserProfileId(out var active)
            || !string.Equals(active, userId, StringComparison.Ordinal)
            || !string.Equals(
                preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty),
                userId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Quiz operations require the current authenticated owner.");
        }
    }

    internal static async Task RequireOwnerReachableWordsAsync(
        string userId, IReadOnlyList<VocabularyWord?> suppliedWords,
        IUserScopeProvider scope, IPreferencesService preferences,
        LearningResourceRepository resources, CancellationToken cancellationToken)
    {
        RequireActiveOwner(userId, scope, preferences, cancellationToken);
        if (suppliedWords.Any(word => word is null || string.IsNullOrWhiteSpace(word.Id)))
            throw new NotSupportedException("Quiz words require real owned vocabulary IDs.");

        if (suppliedWords.Count == 0)
            return;

        // This repository queries the resource-word mapping with an explicit
        // owner ID. Never infer ownership from the caller's word objects.
        var owned = await resources.GetAllVocabularyWordsWithResourcesAsync(userId);
        RequireActiveOwner(userId, scope, preferences, cancellationToken);
        var byId = owned.ToDictionary(word => word.Id, StringComparer.Ordinal);

        foreach (var word in suppliedWords)
        {
            if (!byId.TryGetValue(word!.Id, out var stored)
                || !string.Equals(stored.TargetLanguageTerm, word.TargetLanguageTerm,
                    StringComparison.Ordinal)
                || !string.Equals(stored.NativeLanguageTerm, word.NativeLanguageTerm,
                    StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "Quiz words must exactly match vocabulary reachable by the current owner.");
            }
        }
    }
}
