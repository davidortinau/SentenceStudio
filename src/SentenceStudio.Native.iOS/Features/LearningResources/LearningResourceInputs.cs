using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed record LearningResourceInputs(
    string QuickQuery,
    string QuickNativeTerm,
    string PasteText,
    string Delimiter,
    bool BulkExpanded,
    bool CreateWordExpanded,
    Action<string> QuickQueryChanged,
    Action<string> QuickNativeTermChanged,
    Action<string> PasteTextChanged,
    Action<string> DelimiterChanged,
    Action ToggleBulk,
    Action ToggleCreateWord,
    Action ChooseFile,
    Action<SwiftView> RegisterQuickSearch,
    Action<SwiftView, bool> QuickFocusChanged,
    Func<ResourceQuickAddKey, bool> QuickKeyPressed,
    Action<SwiftView> RegisterImport,
    Action<SwiftView> RegisterSave,
    Action<SwiftView> RegisterStagedGrid,
    Action<SwiftView> RegisterStagedCount,
    Action<SwiftView> RegisterVocabularySection);

