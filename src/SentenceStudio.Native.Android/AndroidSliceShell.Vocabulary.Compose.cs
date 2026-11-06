using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.UI.Graphics;
using AndroidX.Compose.UI.Graphics.Painter;
using SentenceStudio.Common;
using SentenceStudio.Native.Features.Vocabulary;
using SentenceStudio.Shared.Models;
using Bitmap = Android.Graphics.Bitmap;
using Color = AndroidX.Compose.Color;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Vocabulary (source Vocabulary.razor and VocabularyWordEdit.razor; iOS
/// VocabularyListView and VocabularyWordEditView) on the shared owner-scoped
/// VocabularyBrowser: search, filters (phone sheet, live controls when wide),
/// sort and view mode, lazy word cards, and the word editor with audio, memory
/// aids, progress, history and resource associations.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private sealed record VocabularyRow(string Key, Func<ComposableNode> Content);

    private (byte[] Source, Bitmap? Bitmap) _vocabularyPhoto;

    private ComposableNode RenderVocabulary(AndroidVisualPalette palette)
    {
        var vocabulary = _vocabulary;
        if (IsRecentLookupsShown)
            return RecentLookupsPage(palette);
        if (vocabulary is { IsFormOpen: true })
            return VocabularyWordPage(vocabulary, palette);
        var page = VocabularyListPage(vocabulary, palette);
        if (vocabulary is { IsPhoneFilterPresented: true })
            return new Box { Modifier.FillMaxSize(), page, VocabularyFilterSheet(vocabulary, palette) };
        return page;
    }

    // ------------------------------------------------------------ List

    private ComposableNode VocabularyListPage(VocabularyBrowser? vocabulary, AndroidVisualPalette palette)
    {
        var state = vocabulary?.ListState;
        var wide = WindowWidthDp >= 768;
        var header = ScreenHeader(Localize("Vocabulary_Title"), palette, back: true,
            backAutomationId: "native.vocabulary.back");
        if (vocabulary is not null && header is TopAppBar bar)
        {
            var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
            {
                // Recent lookups (decision 4 B): words looked up in Writing and Reading.
                RecentLookupsHeaderAction(palette, enabled: state is { Blocked: false }),
            };
            if (!wide)
            {
                var label = Localize("Vocabulary_Filters");
                var active = state?.Filters.HasActiveFilters == true;
                var shown = active ? $"{label} ({state!.Filters.ActiveFilterCount})" : label;
                var filters = new TextButton(vocabulary.OpenPhoneFilters,
                    enabled: state is { Blocked: false, Loading: false })
                {
                    Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                        .TestTag("native.vocabulary.filters.open")
                        .Semantics(semantics => semantics.ContentDescription(shown)),
                };
                filters.Add(new Text(shown) { Color = active ? palette.Primary : palette.Text });
                actions.Add(filters);
            }
            bar.Actions = actions;
        }
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.vocabulary.page"),
            header,
        };
        var rows = new List<VocabularyRow>();
        if (state is null)
        {
            rows.Add(new("status", () => StatusAlert(Localize("Vocabulary_NoActiveProfile"),
                palette, error: true, automationId: "native.vocabulary.status")));
            page.Add(VocabularyLazyList(rows, wide, listMode: true));
            return page;
        }

        var search = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.search.row"),
            new OutlinedTextField(state.Filters.CommittedQuery, value =>
            {
                vocabulary!.Search(value);
                RenderMoreNow();
            }, enabled: !state.Blocked, singleLine: true)
            {
                Modifier = Modifier.Weight(1f).TestTag("native.vocabulary.search")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize("Vocabulary_SearchPlaceholder"))),
                Placeholder = new Text(Localize("Vocabulary_SearchPlaceholder")),
                LeadingIcon = new Icon(Icons.Default.Search, Localize("Vocabulary_SearchPlaceholder"))
                {
                    Modifier = Modifier.Size(20),
                    Tint = palette.SecondaryText,
                },
            },
        };
        if (state.Filters.HasActiveFilters)
        {
            var clear = new IconButton(vocabulary!.ClearFilters, enabled: true)
            {
                Modifier = Modifier.TestTag("native.vocabulary.search.clear")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize("Vocabulary_ClearFilters"))),
            };
            clear.Add(new Icon(Icons.Default.Clear, Localize("Vocabulary_ClearFilters"))
            {
                Modifier = Modifier.Size(22),
                Tint = palette.Text,
            });
            search.Add(clear);
        }
        page.Add(new Column(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
            search,
        });

        if (state.Loading)
            rows.Add(new("status", () => new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.status"),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                Body(Localize("Common_Loading"), palette, secondary: true),
            }));
        else if (state.Blocked || state.ErrorKey is not null)
        {
            rows.Add(new("status", () => StatusAlert(
                Localize(state.ErrorKey ?? "Vocabulary_NoActiveProfile"), palette, error: true,
                automationId: "native.vocabulary.status")));
            if (!state.Blocked)
                rows.Add(new("retry", () => Action(Localize("Common_Retry"), "native.vocabulary.retry",
                    () => RunMoreOnUi(vocabulary!.ReloadAsync), true, primary: false)));
        }
        else
        {
            if (wide)
                rows.Add(new("wide", () => VocabularyWideControls(vocabulary!, state, palette)));
            if (!string.IsNullOrWhiteSpace(state.Feedback))
                rows.Add(new("feedback", () => state.FeedbackWarning
                    ? StatusAlert(state.Feedback!, palette, error: true,
                        automationId: "native.vocabulary.feedback")
                    : StatusAlert(state.Feedback!, palette, success: true,
                        automationId: "native.vocabulary.feedback")));
            if (state.Duplicates is { } duplicates)
                rows.Add(new("duplicates", () =>
                    VocabularyDuplicatesPanel(vocabulary!, duplicates, palette)));
            if (state.FilteredWords.Count == 0)
                rows.Add(new("empty", () =>
                {
                    var message = Body(Localize(state.Catalog.Words.Count == 0
                            ? "Vocabulary_NoWordsYet" : "Vocabulary_NoMatchFilter"), palette,
                        secondary: true, modifier: Modifier.FillMaxWidth().Padding(48)
                            .TestTag("native.vocabulary.status"));
                    message.Align = TextAlign.Center;
                    return message;
                }));
        }
        if (!state.Loading && !state.Blocked && state.ErrorKey is null
            && state.FilteredWords.Count > 0)
        {
            var statusRows = rows.ToArray();
            var listMode = !wide || state.ListMode;
            if (listMode)
            {
                foreach (var word in state.FilteredWords)
                {
                    var item = word;
                    rows.Add(new($"word.{item.Id}", () => VocabularyCard(item, vocabulary!, palette)));
                }
                page.Add(VocabularyLazyList(rows, wide, listMode: true));
            }
            else if (state.Duplicates is not null)
            {
                // One scroll container while the duplicate manager is open (the source panel
                // scrolls with the page): the grid's rows become lazy rows below it.
                var columns = WindowWidthDp < 992 ? 2 : 3;
                var words = state.FilteredWords;
                for (var start = 0; start < words.Count; start += columns)
                {
                    var slice = words.Skip(start).Take(columns).ToArray();
                    rows.Add(new($"grid.{slice[0].Id}", () =>
                    {
                        var row = new Row(Arrangement.SpacedBy(12)) { Modifier.FillMaxWidth() };
                        foreach (var word in slice)
                            row.Add(new Box { Modifier.Weight(1f), VocabularyCard(word, vocabulary!, palette) });
                        for (var pad = slice.Length; pad < columns; pad++)
                            row.Add(new Box { Modifier.Weight(1f) });
                        return row;
                    }));
                }
                page.Add(VocabularyLazyList(rows, wide, listMode: true));
            }
            else
            {
                if (statusRows.Length > 0)
                {
                    var top = new Column(Arrangement.SpacedBy(10))
                    {
                        Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding),
                    };
                    foreach (var row in statusRows)
                        top.Add(row.Content());
                    page.Add(top);
                }
                var columns = WindowWidthDp < 992 ? 2 : 3;
                page.Add(new LazyVerticalGrid<NativeVocabularyEntry>(GridCells.Fixed(columns),
                    state.FilteredWords.ToArray(), word => VocabularyCard(word, vocabulary!, palette))
                {
                    Key = word => word.Id,
                    Modifier = Modifier.FillMaxWidth().Weight(1f)
                        .Padding(horizontal: AndroidVisualTheme.PagePadding)
                        .TestTag("native.vocabulary.cards"),
                    VerticalArrangement = Arrangement.SpacedBy(12),
                    HorizontalArrangement = Arrangement.SpacedBy(12),
                });
            }
            return page;
        }
        page.Add(VocabularyLazyList(rows, wide, listMode: true));
        return page;
    }

    private static ComposableNode VocabularyLazyList(List<VocabularyRow> rows, bool wide, bool listMode) =>
        new LazyColumn<VocabularyRow>(rows, row => row.Content())
        {
            Key = row => row.Key,
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .Padding(horizontal: AndroidVisualTheme.PagePadding)
                .TestTag("native.vocabulary.cards"),
            VerticalArrangement = Arrangement.SpacedBy(12),
        };

    private ComposableNode VocabularyWideControls(VocabularyBrowser vocabulary,
        VocabularyBrowserList state, AndroidVisualPalette palette)
    {
        var words = state.FilteredWords;
        var stats = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.stats"),
        };
        foreach (var (key, count) in new (string, int)[]
        {
            ("Vocabulary_StatsUnknown", words.Count(word => word.Status == LearningStatus.Unknown)),
            ("Vocabulary_StatsLearning", words.Count(word => word.Status == LearningStatus.Learning)),
            ("Vocabulary_StatsFamiliar", words.Count(word => word.Status == LearningStatus.Familiar)),
            ("Vocabulary_StatsKnown", words.Count(word => word.Status == LearningStatus.Known)),
        })
            stats.Add(Caption(string.Format(CultureInfo.CurrentCulture, Localize(key), count), palette));
        stats.Add(Caption(string.Format(CultureInfo.CurrentCulture, Localize("Vocabulary_StatsTotal"),
            words.Count, state.Catalog.Words.Count), palette));
        var sortLabel = (string key, string field) => state.SortField == field
            ? $"{Localize(key)} {(state.SortDescending ? "↓" : "↑")}" : Localize(key);
        var controls = new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Box
            {
                Modifier.Weight(1f),
                MoreSegments("native.vocabulary.sort",
                    [
                        ("added", sortLabel("Vocabulary_SortAdded", "added")),
                        ("updated", sortLabel("Vocabulary_SortUpdated", "updated")),
                    ], state.SortField, field => vocabulary.SetSort(field), true,
                    // Source: tapping the active sort flips its direction.
                    allowReselect: true),
            },
            new Box
            {
                Modifier.Weight(1f),
                MoreSegments("native.vocabulary.view",
                    [
                        ("grid", Localize("Vocabulary_GridView")),
                        ("list", Localize("Vocabulary_ListView")),
                    ], state.ListMode ? "list" : "grid",
                    mode => vocabulary.SetListMode(mode == "list"), true),
            },
        };
        var column = new Column(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.wide.controls"),
            stats,
            controls,
        };
        var fields = VocabularyFilterFields(vocabulary, state, palette, live: true);
        for (var index = 0; index < fields.Count; index += 2)
        {
            var row = new Row(Arrangement.SpacedBy(10)) { Modifier.FillMaxWidth() };
            row.Add(new Box { Modifier.Weight(1f), fields[index] });
            row.Add(index + 1 < fields.Count
                ? new Box { Modifier.Weight(1f), fields[index + 1] }
                : new Box { Modifier.Weight(1f) });
            column.Add(row);
        }
        return column;
    }

    private ComposableNode VocabularyCard(NativeVocabularyEntry word, VocabularyBrowser vocabulary,
        AndroidVisualPalette palette)
    {
        var status = Localize(VocabularyStatusKey(word));
        var metadata = new List<string>
        {
            word.AssociatedResources.Count == 0
                ? Localize("Vocabulary_OrphanedBadge")
                : string.Format(CultureInfo.CurrentCulture, Localize("Vocabulary_ResourcesCount"),
                    word.AssociatedResources.Count),
        };
        if (!string.IsNullOrWhiteSpace(word.Lemma))
            metadata.Add(word.Lemma);
        if (!string.IsNullOrWhiteSpace(word.Tags))
            metadata.AddRange(word.Tags.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(3));
        var details = $"{Localize(EncodingKey(word.EncodingLabel))} · {string.Join(" · ", metadata)}";
        var badge = new Surface
        {
            Modifier = Modifier.TestTag($"native.vocabulary.card.{word.Id}.status"),
            Shape = Shape.RoundedCorners(12),
            Color = word.IsKnown ? palette.Success : palette.Elevated,
        };
        badge.Add(new Text(status)
        {
            Modifier = Modifier.Padding(horizontal: 10, vertical: 4),
            Color = word.IsKnown ? Color.White : palette.Text,
            FontSize = new Sp(AndroidVisualTheme.CaptionSize),
        });
        var target = Heading(word.TargetTerm, palette, 20,
            Modifier.TestTag($"native.vocabulary.card.{word.Id}.target"));
        var id = word.Id;
        var card = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Clip(Shape.RoundedCorners(AndroidVisualTheme.CardRadius))
                .Clickable(() => RunMoreOnUi(() => vocabulary.OpenWordAsync(id)))
                .TestTag($"native.vocabulary.card.{word.Id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{word.TargetTerm}, {word.NativeTerm}, {status}");
                    semantics.Role(SemanticsRole.Button);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Padding(AndroidVisualTheme.CardPadding),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.Top)
            {
                Modifier.FillMaxWidth(),
                new Column(Arrangement.SpacedBy(2))
                {
                    Modifier.Weight(1f),
                    target,
                    Body(word.NativeTerm, palette, secondary: true,
                        modifier: Modifier.TestTag($"native.vocabulary.card.{word.Id}.native")),
                },
                badge,
            },
            Caption(details, palette, $"native.vocabulary.card.{word.Id}.details"),
        });
        return card;
    }

    private static string VocabularyStatusKey(NativeVocabularyEntry word) =>
        word.IsKnown ? "Vocabulary_Known" : word.Status switch
        {
            LearningStatus.Familiar => "Vocabulary_Familiar",
            LearningStatus.Learning => "Vocabulary_Learning",
            LearningStatus.Known => "Vocabulary_Known",
            _ => "Vocabulary_Unknown"
        };

    private static string EncodingKey(string encoding) => encoding switch
    {
        "Strong" => "Vocabulary_Strong",
        "Good" => "Vocabulary_Good",
        _ => "Vocabulary_Basic"
    };

    // ------------------------------------------------------------ Filters

    private ComposableNode VocabularyFilterSheet(VocabularyBrowser vocabulary, AndroidVisualPalette palette) =>
        new Composed(composer => VocabularyFilterSheet(vocabulary, palette,
            composer.Remember(() => new AndroidX.Compose.ScrollState())));

    private ComposableNode VocabularyFilterSheet(VocabularyBrowser vocabulary, AndroidVisualPalette palette,
        AndroidX.Compose.ScrollState scroll)
    {
        var state = vocabulary.ListState;
        var sheet = new ModalBottomSheet(onDismissRequest: vocabulary.CancelPhoneFilters)
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag("native.vocabulary.filters.sheet")),
        };
        var close = new IconButton(vocabulary.CancelPhoneFilters, enabled: true)
        {
            Modifier = Modifier.TestTag("native.vocabulary.filters.close")
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Close"))),
        };
        close.Add(new Icon(Icons.Default.Close, Localize("Common_Close"))
        {
            Modifier = Modifier.Size(22),
            Tint = palette.Text,
        });
        var content = new Column(Arrangement.SpacedBy(14))
        {
            Modifier.FillMaxWidth().VerticalScroll(scroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: AndroidVisualTheme.CardPadding)
                .TestTag("native.vocabulary.filters.sheet.content"),
            new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.filters.heading"),
                Heading(Localize("Vocabulary_Filters"), palette, 20,
                    Modifier.Semantics(semantics => semantics.Heading())),
                close,
            },
        };
        foreach (var field in VocabularyFilterFields(vocabulary, state, palette, live: false))
            content.Add(field);
        content.Add(Action(Localize("Vocabulary_ClearAllFilters"), "native.vocabulary.filters.clear",
            vocabulary.ClearFilters, true, primary: false));
        content.Add(new Row(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.filters.actions"),
            new Box { Modifier.Weight(1f), Action(Localize("Common_Cancel"),
                "native.vocabulary.filters.cancel", vocabulary.CancelPhoneFilters, true, primary: false) },
            new Box { Modifier.Weight(1f), Action(Localize("Vocabulary_BulkApply"),
                "native.vocabulary.filters.apply", vocabulary.ApplyPhoneFilters, true) },
        });
        sheet.Add(content);
        return sheet;
    }

    /// <summary>The source filter selects, in source order, offering only values the catalog has.</summary>
    private List<ComposableNode> VocabularyFilterFields(VocabularyBrowser vocabulary,
        VocabularyBrowserList state, AndroidVisualPalette palette, bool live)
    {
        var words = state.Catalog.Words;
        var tags = words.Where(word => !string.IsNullOrWhiteSpace(word.Tags))
            .SelectMany(word => word.Tags!.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var lemmas = words.Select(word => word.Lemma)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var languages = words
            .Select(word => word.Language ?? word.AssociatedResources
                .FirstOrDefault(resource => !string.IsNullOrWhiteSpace(resource.Language))?.Language)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var fields = new List<ComposableNode>
        {
            VocabularyFilter(vocabulary, state, "association", "Vocabulary_Association",
                "Vocabulary_AllWords", ["associated", "orphaned"], live, palette,
                value => Localize(value == "associated" ? "Vocabulary_Associated" : "Vocabulary_Orphaned")),
            VocabularyFilter(vocabulary, state, "type", "NativeResources_TypeFilter",
                "Vocabulary_FilterTypeAll", ["word", "phrase", "sentence"], live, palette,
                value => Localize(value switch
                {
                    "word" => "Vocabulary_FilterTypeWord",
                    "phrase" => "Vocabulary_FilterTypePhrase",
                    _ => "Vocabulary_FilterTypeSentence"
                })),
            VocabularyFilter(vocabulary, state, "status", "Vocabulary_StatusLabel",
                "Vocabulary_AllStatus", ["known", "familiar", "learning", "unknown"], live, palette,
                value => Localize(value switch
                {
                    "known" => "Vocabulary_Known",
                    "familiar" => "Vocabulary_Familiar",
                    "learning" => "Vocabulary_Learning",
                    _ => "Vocabulary_Unknown"
                })),
        };
        if (state.Catalog.Resources.Count > 0)
            fields.Add(VocabularyFilter(vocabulary, state, "resource", "Vocabulary_ResourceLabel",
                "Vocabulary_AllResources", state.Catalog.Resources.Select(resource => resource.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), live, palette));
        if (tags.Length > 0)
            fields.Add(VocabularyFilter(vocabulary, state, "tag", "Vocabulary_TagLabel",
                "Vocabulary_AllTags", tags, live, palette));
        if (lemmas.Length > 0)
            fields.Add(VocabularyFilter(vocabulary, state, "lemma", "Vocabulary_LemmaLabel",
                "Vocabulary_AllLemmas", lemmas, live, palette));
        if (languages.Length > 0)
            fields.Add(VocabularyFilter(vocabulary, state, "language", "Vocabulary_LanguageLabel",
                "Vocabulary_AllLanguages", languages, live, palette));
        fields.Add(VocabularyFilter(vocabulary, state, "encoding", "Vocabulary_EncodingStrength",
            "Vocabulary_AllEncoding", ["strong", "good", "basic"], live, palette,
            value => Localize(value switch
            {
                "strong" => "Vocabulary_Strong",
                "good" => "Vocabulary_Good",
                _ => "Vocabulary_Basic"
            })));
        return fields;
    }

    private ComposableNode VocabularyFilter(VocabularyBrowser vocabulary, VocabularyBrowserList state,
        string type, string labelKey, string allKey, IReadOnlyList<string> values, bool live,
        AndroidVisualPalette palette, Func<string, string>? valueLabel = null)
    {
        var current = state.Filters.FilterValue(type, useDraft: !live);
        var known = values.Count;
        // A restored query can name a value the catalog no longer has; keep it selectable.
        if (!string.IsNullOrWhiteSpace(current)
            && !values.Any(value => value.Equals(current, StringComparison.OrdinalIgnoreCase)))
            values = [.. values, current];
        var options = new List<(string Value, string Label)> { (string.Empty, Localize(allKey)) };
        for (var index = 0; index < values.Count; index++)
            options.Add((values[index], index < known
                ? valueLabel?.Invoke(values[index]) ?? values[index] : values[index]));
        var selected = values.FirstOrDefault(value =>
            value.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return MoreDropdown($"vocabulary.filter.{type}.{(live ? "live" : "sheet")}",
            $"native.vocabulary.filter.{type}", Localize(labelKey), options, selected,
            value => vocabulary.ChangeFilter(type, value.Length == 0 ? null : value, live),
            true, palette);
    }

    // ------------------------------------------------------------ Word editor

    private ComposableNode VocabularyWordPage(VocabularyBrowser vocabulary, AndroidVisualPalette palette)
    {
        var state = vocabulary.EditState;
        var header = ScreenHeader(Localize("VocabWordEdit_EditTitle"), palette, back: true,
            backAutomationId: "native.vocabulary.edit.back");
        if (state.Detail is not null && state.Draft is not null && header is TopAppBar bar)
            bar.Actions = VocabularyWordActions(vocabulary, state, palette);
        var rows = new List<VocabularyRow>();
        void Add(string key, Func<ComposableNode> content) => rows.Add(new(key, content));
        if (state.Loading)
            Add("loading", () => new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.status"),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                Body(Localize("Common_Loading"), palette, secondary: true),
            });
        if (state.ErrorKey is { } error)
            Add("error", () => StatusAlert(Localize(error), palette, error: true,
                automationId: "native.vocabulary.edit.error"));
        if (state.DeleteError is { } deleteError)
            Add("delete.error", () => StatusAlert(deleteError, palette, error: true,
                automationId: "native.vocabulary.edit.delete.error"));
        if (state.Detail is not { } detail || state.Draft is not { } draft)
        {
            if (!state.Loading)
                Add("retry", () => Action(Localize("Common_Retry"), "native.vocabulary.edit.retry",
                    () => RunMoreOnUi(vocabulary.RetryWordAsync), true, primary: false));
            Add("cancel", () => Action(Localize("Common_Cancel"), "native.vocabulary.edit.cancel",
                vocabulary.CloseWord, true, primary: false));
            return VocabularyWordColumn(header, rows);
        }
        var editable = !state.Saving;
        if (state.Saving)
            Add("saving", () => new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.saving"),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                Body(Localize("Common_Save"), palette, secondary: true),
            });

        Add("terms", () => Card(palette, "native.vocabulary.edit.terms",
            VocabularyTermsHeading(vocabulary, state, palette),
            VocabularyTargetField(vocabulary, state, draft, palette),
            WordField(vocabulary, "VocabWordEdit_NativeLanguageTerm", "VocabWordEdit_NativePlaceholder",
                "native.vocabulary.edit.native", draft.NativeTerm, nameof(draft.NativeTerm),
                editable, palette)));
        Add("encoding", () => VocabularyEncodingCard(vocabulary, state, detail, draft,
            editable, palette));
        if (detail.Examples.Count > 0)
            Add("examples", () =>
            {
                var items = new List<ComposableNode>
                {
                    WordSectionHeading("NativeVocabulary_ReferenceSentences", palette),
                };
                foreach (var example in detail.Examples)
                {
                    items.Add(Body(example.TargetSentence, palette,
                        modifier: Modifier.TestTag($"native.vocabulary.edit.example.{example.Id}.target")));
                    items.Add(Body(example.NativeSentence ?? string.Empty, palette, secondary: true,
                        modifier: Modifier.TestTag($"native.vocabulary.edit.example.{example.Id}.native")));
                }
                return Card(palette, "native.vocabulary.edit.examples", items.ToArray());
            });
        if (draft.LexicalUnitType is LexicalUnitType.Phrase or LexicalUnitType.Sentence
            || detail.HasConstituentLinks)
            Add("constituents", () =>
            {
                var items = new List<ComposableNode>
                {
                    WordSectionHeading("NativeVocabulary_Constituents", palette),
                };
                if (detail.HasConstituentLinks)
                    items.Add(Caption(Localize("NativeVocabulary_ConstituentsLocked"), palette,
                        "native.vocabulary.edit.constituents.warning"));
                if (detail.Constituents.Count == 0)
                    items.Add(Body(Localize("NativeVocabulary_NoConstituents"), palette, secondary: true));
                foreach (var constituent in detail.Constituents)
                    items.Add(Body($"{constituent.TargetTerm} · {constituent.NativeTerm}", palette,
                        modifier: Modifier.TestTag($"native.vocabulary.edit.constituent.{constituent.Id}")));
                return Card(palette, "native.vocabulary.edit.constituents", items.ToArray());
            });
        Add("progress", () => VocabularyProgressCard(detail, palette));
        // History: a heading row, then one lazy row per learning event.
        Add("history", () => VocabularyHistoryHeading(detail, palette));
        foreach (var item in detail.History)
        {
            var entry = item;
            Add($"history.{entry.Id}", () => VocabularyHistoryRow(entry, palette));
        }
        Add("resources", () => VocabularyResourcesCard(vocabulary, detail, draft,
            editable, palette));
        Add("actions", () => new Row(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.actions"),
            new Box { Modifier.Weight(1f), Action(Localize("Common_Cancel"),
                "native.vocabulary.edit.cancel", vocabulary.CloseWord, !state.Saving, primary: false) },
            new Box { Modifier.Weight(1f), Action(Localize("Common_Save"),
                "native.vocabulary.edit.save", () => RunMoreOnUi(vocabulary.SaveAsync), state.CanSave) },
        });
        // The More menu's Delete is disabled for a word the server would keep; this says why.
        if (VocabularyWordCommands.DeleteBlockedReason(detail, Localize) is { } deleteBlocked)
            Add("delete.blocked", () => Caption(deleteBlocked, palette,
                "native.vocabulary.edit.delete.blocked"));
        var page = VocabularyWordColumn(header, rows);
        if (vocabulary.IsDeleteConfirmationPresented)
            page.Add(VocabularyDeleteDialog(vocabulary, state, detail, palette));
        return page;
    }

    private static Column VocabularyWordColumn(ComposableNode header, List<VocabularyRow> rows) =>
        new Column
        {
            Modifier.FillMaxSize().TestTag("native.vocabulary.edit.page"),
            header,
            new LazyColumn<VocabularyRow>(rows, row => row.Content())
            {
                Key = row => row.Key,
                Modifier = Modifier.FillMaxWidth().Weight(1f)
                    .Padding(horizontal: AndroidVisualTheme.PagePadding)
                    .TestTag("native.vocabulary.edit.scroll"),
                VerticalArrangement = Arrangement.SpacedBy(12),
            },
        };

    private static ComposableNode WordSectionHeading(string key, AndroidVisualPalette palette) =>
        Heading(Localize(key), palette, 20, Modifier.TestTag($"native.vocabulary.edit.section.{key}")
            .Semantics(semantics => semantics.Heading()));

    private ComposableNode WordField(VocabularyBrowser vocabulary, string labelKey, string placeholderKey,
        string tag, string value, string property, bool editable,
        AndroidVisualPalette palette, int lines = 1)
    {
        var label = Localize(labelKey);
        return new OutlinedTextField(value, text =>
        {
            vocabulary.ChangeDraft(property, text);
            RenderMoreNow();
        }, enabled: editable, singleLine: lines == 1, minLines: lines)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(label),
            Placeholder = new Text(Localize(placeholderKey)),
        };
    }

    private ComposableNode VocabularyTargetField(VocabularyBrowser vocabulary, VocabularyBrowserEdit state,
        NativeVocabularyEditDraft draft, AndroidVisualPalette palette)
    {
        var label = Localize("VocabWordEdit_PlayAudio");
        var play = new IconButton(() => RunMoreOnUi(vocabulary.PlayAudioAsync),
            enabled: !state.PlayingAudio && !state.Saving && !string.IsNullOrWhiteSpace(draft.TargetTerm))
        {
            Modifier = Modifier.TestTag("native.vocabulary.edit.audio")
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        play.Add(new Icon(Icons.Default.PlayArrow, label)
        {
            Modifier = Modifier.Size(26),
            Tint = state.PlayingAudio ? palette.Primary : palette.Text,
        });
        var field = new OutlinedTextField(draft.TargetTerm, text =>
        {
            vocabulary.ChangeDraft(nameof(draft.TargetTerm), text);
            RenderMoreNow();
        }, enabled: !state.Saving, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag("native.vocabulary.edit.target")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("VocabWordEdit_TargetLanguageTerm"))),
            Label = new Text(Localize("VocabWordEdit_TargetLanguageTerm")),
            Placeholder = new Text(Localize("VocabWordEdit_TargetPlaceholder")),
        };
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.target.field"),
            field,
            play,
        };
    }

    private ComposableNode VocabularyEncodingCard(VocabularyBrowser vocabulary, VocabularyBrowserEdit state,
        NativeVocabularyDetail detail, NativeVocabularyEditDraft draft, bool editable,
        AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>
        {
            WordSectionHeading("VocabWordEdit_EncodingMemoryAids", palette),
            Body($"{Localize("VocabWordEdit_EncodingStrength")} {Localize(EncodingKey(detail.Word.EncodingLabel))}",
                palette, modifier: Modifier.TestTag("native.vocabulary.edit.encoding")),
            WordField(vocabulary, "VocabWordEdit_Lemma", "VocabWordEdit_LemmaPlaceholder",
                "native.vocabulary.edit.lemma", draft.Lemma ?? string.Empty, nameof(draft.Lemma),
                editable, palette),
        };
        var languages = new[] { string.Empty }.Concat(Constants.Languages)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedLanguage = languages.FirstOrDefault(value =>
            value.Equals(draft.Language ?? string.Empty, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        items.Add(MoreDropdown("vocabulary.edit.language", "native.vocabulary.edit.language",
            Localize("VocabWordEdit_Language"),
            languages.Select(value => (value, value.Length == 0 ? Localize("VocabWordEdit_NotSet") : value))
                .ToArray(),
            selectedLanguage, value =>
            {
                vocabulary.ChangeDraft(nameof(draft.Language), value);
                RenderMoreNow();
            }, editable, palette));
        var types = draft.LexicalUnitType is LexicalUnitType.Unknown or LexicalUnitType.Idiom
            ? new[] { draft.LexicalUnitType, LexicalUnitType.Word, LexicalUnitType.Phrase, LexicalUnitType.Sentence }
            : new[] { LexicalUnitType.Word, LexicalUnitType.Phrase, LexicalUnitType.Sentence };
        items.Add(MoreDropdown("vocabulary.edit.type", "native.vocabulary.edit.type",
            Localize("NativeResources_TypeFilter"),
            types.Select(value => (value.ToString(), Localize(value switch
            {
                LexicalUnitType.Phrase => "Vocabulary_FilterTypePhrase",
                LexicalUnitType.Sentence => "Vocabulary_FilterTypeSentence",
                LexicalUnitType.Unknown => "VocabWordEdit_Unknown",
                LexicalUnitType.Idiom => "NativeResources_LexicalIdiom",
                _ => "Vocabulary_FilterTypeWord"
            }))).ToArray(),
            draft.LexicalUnitType.ToString(), value =>
            {
                vocabulary.ChangeDraft(nameof(draft.LexicalUnitType), value);
                RenderMoreNow();
            }, editable && !detail.HasConstituentLinks, palette));
        items.Add(WordField(vocabulary, "VocabWordEdit_Tags", "VocabWordEdit_TagsPlaceholder",
            "native.vocabulary.edit.tags", draft.Tags ?? string.Empty, nameof(draft.Tags),
            editable, palette));
        items.Add(WordField(vocabulary, "VocabWordEdit_MnemonicStory", "VocabWordEdit_MnemonicStory",
            "native.vocabulary.edit.mnemonic", draft.MnemonicText ?? string.Empty, nameof(draft.MnemonicText),
            editable, palette, lines: 4));
        items.Add(WordField(vocabulary, "VocabWordEdit_MnemonicImageUrl", "VocabWordEdit_MnemonicImageUrl",
            "native.vocabulary.edit.image.url", draft.MnemonicImageUri ?? string.Empty,
            nameof(draft.MnemonicImageUri), editable, palette));
        if (state.Photo is { } photo && VocabularyPhotoPainter(photo.Bytes) is { } painter)
            items.Add(new Image(painter, Localize("VocabWordEdit_MnemonicAlt"))
            {
                Modifier = Modifier.FillMaxWidth().Height(120)
                    .TestTag("native.vocabulary.edit.image.preview"),
                ContentScale = ContentScale.Fit,
            });
        return Card(palette, "native.vocabulary.edit.memory", items.ToArray());
    }

    private Painter? VocabularyPhotoPainter(byte[] bytes)
    {
        if (!ReferenceEquals(_vocabularyPhoto.Source, bytes))
        {
            _vocabularyPhoto.Bitmap?.Dispose();
            _vocabularyPhoto = (bytes, DecodeSampled(bytes, 1024));
        }
        if (_vocabularyPhoto.Bitmap is not { } bitmap)
            return null;
        return BitmapPainterKt.BitmapPainter(
            image: AndroidImageBitmap_androidKt.AsImageBitmap(bitmap), srcOffset: 0L,
            srcSize: ((long)bitmap.Width << 32) | (uint)bitmap.Height, filterQuality: 1);
    }

    private ComposableNode VocabularyProgressCard(NativeVocabularyDetail detail, AndroidVisualPalette palette)
    {
        var progress = detail.Progress;
        var status = Localize(detail.Word.IsKnown ? "VocabWordEdit_Known" : detail.Word.Status switch
        {
            LearningStatus.Known => "VocabWordEdit_Known",
            LearningStatus.Familiar => "VocabWordEdit_Familiar",
            LearningStatus.Learning => "VocabWordEdit_Learning",
            _ => "VocabWordEdit_Unknown"
        });
        // NextReviewDate is a UTC instant; "due now / tomorrow" is the learner's local day.
        var today = DateTime.Now.Date;
        var next = progress?.NextReviewDate?.ToLocalTime() is not { } review
            ? Localize("VocabWordEdit_NotScheduled")
            : review.Date <= today
                ? Localize("VocabWordEdit_DueNow")
                : review.Date == today.AddDays(1)
                    ? Localize("VocabWordEdit_Tomorrow")
                    : review.Date <= today.AddDays(7)
                        ? string.Format(CultureInfo.CurrentCulture, Localize("VocabWordEdit_DaysAway"),
                            (review.Date - today).Days)
                        : review.ToString("MMM d", CultureInfo.CurrentCulture);
        var summary = progress is null
            ? Localize("VocabWordEdit_StartPracticing")
            : string.Join("\n",
                $"{Localize("NativeVocabQuiz_Details_CurrentStreak")}: "
                    + progress.CurrentStreak.ToString("F1", CultureInfo.CurrentCulture),
                $"{Localize("NativeVocabQuiz_Details_ProductionInStreak")}: {progress.ProductionInStreak}/2",
                $"{Localize("NativeVocabQuiz_Details_MasteryScore")}: "
                    + progress.MasteryScore.ToString("P0", CultureInfo.CurrentCulture));
        ComposableNode Value(string key, string value, string tag) => new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().TestTag($"{tag}.row"),
            Caption(Localize(key), palette),
            Body(value, palette, modifier: Modifier.TestTag(tag)),
        };
        return Card(palette, "native.vocabulary.edit.progress",
            WordSectionHeading("VocabWordEdit_LearningProgress", palette),
            Value("VocabWordEdit_Status", detail.Word.IsKnown
                ? $"{status} · {Localize("VocabWordEdit_EarnedThrough")}" : status,
                "native.vocabulary.edit.progress.status"),
            Value("VocabWordEdit_Details", summary, "native.vocabulary.edit.progress.details"),
            Value("VocabWordEdit_NextReview", next, "native.vocabulary.edit.progress.review"));
    }

    private ComposableNode VocabularyHistoryHeading(NativeVocabularyDetail detail, AndroidVisualPalette palette)
    {
        var column = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.FillMaxWidth().TestTag("native.vocabulary.edit.history"),
            WordSectionHeading("VocabWordEdit_HistoryTitle", palette),
        };
        if (detail.History.Count == 0)
            column.Add(Body(Localize("VocabWordEdit_HistoryEmpty"), palette, secondary: true,
                modifier: Modifier.TestTag("native.vocabulary.edit.history.empty")));
        else
        {
            var attempts = detail.History.Count(item => !item.IsPassive);
            column.Add(Caption(string.Format(CultureInfo.CurrentCulture,
                    Localize("VocabWordEdit_HistorySummary"), attempts, detail.History.Count - attempts),
                palette, "native.vocabulary.edit.history.count"));
        }
        return column;
    }

    private ComposableNode VocabularyHistoryRow(NativeVocabularyHistory item, AndroidVisualPalette palette) =>
        Body($"{Localize(item.IsPassive ? "VocabWordEdit_HistoryExposure"
                : item.WasCorrect ? "VocabWordEdit_HistoryCorrect" : "VocabWordEdit_HistoryIncorrect")} · "
            + $"{(item.Activity.Length == 0 ? Localize("VocabWordEdit_HistoryPractice") : item.Activity)} "
            + $"· {item.LearnedAt.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}"
            + (string.IsNullOrWhiteSpace(item.ResourceTitle) ? string.Empty : $" · {item.ResourceTitle}"),
            palette, secondary: item.IsPassive,
            modifier: Modifier.TestTag($"native.vocabulary.edit.history.{item.Id}"));

    private ComposableNode VocabularyResourcesCard(VocabularyBrowser vocabulary, NativeVocabularyDetail detail,
        NativeVocabularyEditDraft draft, bool editable, AndroidVisualPalette palette)
    {
        var selected = draft.ResourceIds.ToHashSet(StringComparer.Ordinal);
        var items = new List<ComposableNode>
        {
            WordSectionHeading("VocabWordEdit_ResourceAssociations", palette),
            Body(string.Format(CultureInfo.CurrentCulture, Localize("VocabWordEdit_SelectedCount"),
                selected.Count), palette, secondary: true,
                modifier: Modifier.TestTag("native.vocabulary.edit.resources.count")),
            Caption(Localize("VocabWordEdit_SelectResources"), palette),
        };
        if (detail.AvailableResources.Count == 0)
            items.Add(Body(Localize("VocabWordEdit_NoResources"), palette, secondary: true));
        foreach (var resource in detail.AvailableResources)
        {
            var id = resource.Id;
            items.Add(SettingSwitch(resource.Title, $"native.vocabulary.edit.resource.{id}",
                selected.Contains(id), value => vocabulary.ToggleResource(id, value),
                editable && !resource.IsSmartResource, palette));
        }
        return Card(palette, "native.vocabulary.edit.resources", items.ToArray());
    }
}
