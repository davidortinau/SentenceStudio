namespace SentenceStudio.Shared.Models;

/// <summary>
/// Who decides that a word is Known (docs/known-words-plan.md, Captain 2026-11-14). The learner's
/// setting, stored as its name on <see cref="UserProfile.LearningMode"/>. It is only ever read:
/// switching it writes no progress row, so the learner's marks stay as they are.
/// </summary>
public enum LearningMode
{
    /// <summary>
    /// The learner and their practice decide: a word they mark Known, or that reaches Known in
    /// practice, stays Known until they choose Practice again.
    /// </summary>
    LearnerLed = 0,

    /// <summary>
    /// Only practice decides: the learner's marks are set aside (kept, not read), and a miss on a
    /// Known word can send it back to practice.
    /// </summary>
    PracticeLed = 1,
}
