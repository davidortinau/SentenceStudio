using SentenceStudio.Abstractions;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Adapters;

public sealed class NativeQuizGraderAdapter(
    IUserScopeProvider scope,
    IPreferencesService preferences) : INativeQuizGrader
{
    public Task<FuzzyMatchResult> EvaluateAsync(
        string userId, string mode, string answer, string expectedAnswer,
        CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        var result = VocabQuizSourcePolicy.EvaluateAnswer(mode, answer, expectedAnswer);
        NativeQuizOwnerGuard.RequireActiveOwner(userId, scope, preferences, cancellationToken);
        return Task.FromResult(result);
    }
}
