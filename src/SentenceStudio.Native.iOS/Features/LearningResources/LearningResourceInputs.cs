using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Common;
using SentenceStudio.Shared.Models;
using DateFormatter = SentenceStudio.Native.iOS.Platform.NativeResourceDateFormatter;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.LearningResources;

internal sealed record LearningResourceInputs(
    string PasteText,
    string Delimiter,
    Action<string> PasteTextChanged,
    Action<string> DelimiterChanged,
    Action ToggleBulk,
    Action OpenAddWords,
    Action ChooseFile,
    int WordWindow,
    Action ShowMoreWords,
    Action<SwiftView> RegisterImport,
    Action<SwiftView> RegisterStagedGrid,
    Action<SwiftView> RegisterVocabularySection);

