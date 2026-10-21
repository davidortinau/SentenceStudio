namespace SentenceStudio.Evaluation.Tests.Infrastructure;

/// <summary>
/// A theory that only runs when SS_RUN_LIVE_EVALS=1. Without it every case reports as skipped,
/// so a plain <c>dotnet test</c> never makes a paid model call by accident.
/// Pair with <c>[Trait("Category", "LiveEval")]</c> so runs can also filter on the category.
/// </summary>
public sealed class LiveEvalTheoryAttribute : TheoryAttribute
{
    public LiveEvalTheoryAttribute()
    {
        if (!LiveEvalSettings.LiveEvalsEnabled)
        {
            Skip = $"Live model evaluation. Set {LiveEvalSettings.GateVariable}=1 to run (makes paid Azure OpenAI calls).";
        }
    }
}
