using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed record LearningResourceActions(
    Action<string, string?> Change,
    Action Save,
    Action Cancel,
    Action Reload,
    Action Retry,
    Action Sync,
    Action RequestDelete,
    Action CancelDelete,
    Action Delete,
    Action Generate,
    Action<string> AttachWord,
    Action CreateWord,
    Action<string> RemoveWord,
    Action<string, LexicalUnitType> SetWordType,
    Action ImportText);

