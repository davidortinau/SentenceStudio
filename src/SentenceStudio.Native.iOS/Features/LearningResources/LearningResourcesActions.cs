using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed record LearningResourcesActions(
    Action<string> Search,
    Action<string?> FilterType,
    Action<string?> FilterLanguage,
    Action<NativeResourceViewMode> SetViewMode,
    Action<string> Open,
    Action Add,
    Action CreateStarter,
    Action Reload,
    Action? OpenSkills = null);
