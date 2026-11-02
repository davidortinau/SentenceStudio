using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Common;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;
using ComposeButton = AndroidX.Compose.Button;
using ComposeScrollState = AndroidX.Compose.ScrollState;

namespace SentenceStudio.Native.Android;

/// <summary>Compose Add/Edit Learning Resource form (fields, vocabulary, quick add, import, delete).</summary>
internal sealed partial class AndroidSliceShell
{
    private ComposeScrollState _resourceFormScroll = null!;

    private ComposableNode RenderResourceForm(
        AndroidResourcesCoordinator resources, AndroidVisualPalette palette)
    {
        var state = resources.FormState;
        var creating = resources.IsCreating;
        var busy = state.IsBusy || Busy.Value;
        var title = creating ? Localize("ResourceAdd_Title")
            : state.Title is { Length: > 0 } named ? named : Localize("Resources_Title");
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(creating
                ? "native.resources.add.screen" : "native.resources.edit.screen"),
            ResourceFormAppBar(resources, state, title, creating, busy, palette),
        };
        var fields = new Column(Arrangement.SpacedBy(14))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_resourceFormScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 12)
                .TestTag("native.resources.form.scroll"),
        };
        page.Add(fields);
        if (state.ShowDeleteConfirmation)
            page.Add(ResourceDeleteDialog(resources, state));

        if (state.IsSaving || state.IsReloading
            || state.Phase == NativeResourceDetailPhase.Deleting)
            fields.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag(creating
                    ? "native.resources.form.status" : "native.resources.form.saving"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(Localize(state.Phase == NativeResourceDetailPhase.Deleting
                    ? "Common_Delete" : "Common_Save"), palette, secondary: true),
            });
        if (state.Status is { Length: > 0 } status)
            fields.Add(StatusAlert(status, palette, success: true,
                automationId: "native.resources.form.status"));
        if (state.ErrorKey is { } error
            && !(state.IsSmartResource
                && state.Phase == NativeResourceDetailPhase.ReadOnly
                && error == "ResourceEdit_AutoUpdated"))
            fields.Add(StatusAlert(FormatResourceError(error), palette, error: true,
                automationId: state.Phase == NativeResourceDetailPhase.Conflict
                    ? NativeAccess.ResourceConflict : "native.resources.form.error"));

        if (state.IsUnavailable)
        {
            if (state.Status is null && state.ErrorKey is null)
                fields.Add(state.Phase == NativeResourceDetailPhase.Loading
                    ? new CircularProgressIndicator
                    {
                        Modifier = Modifier.Size(28).TestTag("native.resources.form.status")
                    }
                    : Body(Localize("NativeResources_LoadError"), palette,
                        modifier: Modifier.TestTag("native.resources.form.status")));
            if (state.Phase == NativeResourceDetailPhase.LoadError)
                fields.Add(Action(Localize(NativeText.Retry), "native.resources.form.retry",
                    resources.RetryForm, !busy, primary: false));
            fields.Add(Action(Localize("Common_Cancel"), NativeAccess.ResourceCancel,
                () => resources.CloseForm(), !busy, primary: false));
            return page;
        }

        if (state.IsSmartResource)
            fields.Add(StatusAlert(
                $"{Localize("ResourceEdit_SmartResource")} {Localize("ResourceEdit_AutoUpdated")}",
                palette, automationId: "native.resources.form.readonly"));

        var readOnly = state.IsSmartResource || busy;
        var basic = new List<ComposableNode>
        {
            Heading(Localize(creating ? "ResourceAdd_BasicInfo" : "ResourceEdit_BasicInfo"),
                palette, 18, Modifier.TestTag("native.resources.form.basic.heading")
                    .Semantics(semantics => semantics.Heading())),
            ResourceField(resources, NativeResourceField.Title,
                Localize(creating ? "ResourceAdd_TitleLabel" : "ResourceEdit_Title"),
                state.Title, NativeAccess.ResourceTitle, readOnly, state.IsSmartResource,
                creating ? Localize("ResourceAdd_TitlePlaceholder") : null, palette),
            ResourceField(resources, NativeResourceField.Description,
                Localize(creating ? "ResourceAdd_DescriptionLabel" : "ResourceEdit_Description"),
                state.Description, NativeAccess.ResourceDescription, readOnly,
                state.IsSmartResource,
                creating ? Localize("ResourceAdd_DescriptionPlaceholder") : null,
                palette, lines: 3),
            ResourceSelect(resources, ResourceMenu.FormMediaType, NativeResourceField.MediaType,
                Localize(creating ? "ResourceAdd_MediaTypeLabel" : "ResourceEdit_MediaType"),
                state.MediaType, Constants.MediaTypes, "native.resources.form.media.type",
                readOnly, state.IsSmartResource, palette),
            ResourceSelect(resources, ResourceMenu.FormLanguage, NativeResourceField.Language,
                Localize(creating ? "ResourceAdd_LanguageLabel" : "ResourceEdit_Language"),
                state.Language, Constants.Languages, "native.resources.form.language",
                readOnly, state.IsSmartResource, palette),
        };
        if (!creating)
            basic.Add(ResourceField(resources, NativeResourceField.MediaUrl,
                Localize("ResourceEdit_MediaUrl"), state.MediaUrl,
                "native.resources.form.media.url", readOnly, state.IsSmartResource,
                null, palette));
        basic.Add(ResourceField(resources, NativeResourceField.Tags,
            Localize(creating ? "ResourceAdd_TagsLabel" : "ResourceEdit_Tags"),
            state.Tags, NativeAccess.ResourceTags, readOnly, state.IsSmartResource,
            creating ? Localize("ResourceAdd_TagsPlaceholder") : null, palette));
        if (!creating)
        {
            basic.Add(LabeledValue(Localize("ResourceEdit_Created"),
                AndroidResourceDates.DetailCreatedDate(state.CreatedAt, state.IsSmartResource),
                "native.resources.form.created", palette));
            basic.Add(LabeledValue(Localize("ResourceEdit_Updated"),
                AndroidResourceDates.DetailUpdatedDate(state.UpdatedAt),
                "native.resources.form.updated", palette));
        }
        fields.Add(Card(palette, "native.resources.form.basic.card", basic.ToArray()));

        if (creating && state.MediaType != "Vocabulary List")
            fields.Add(Card(palette, "native.resources.form.media.card",
                Heading(Localize("ResourceAdd_MediaContent"), palette, 18,
                    Modifier.Semantics(semantics => semantics.Heading())),
                ResourceField(resources, NativeResourceField.MediaUrl,
                    Localize("ResourceAdd_MediaUrlLabel"), state.MediaUrl,
                    "native.resources.form.media.url", readOnly, false,
                    "https://...", palette),
                ResourceField(resources, NativeResourceField.Transcript,
                    Localize("ResourceAdd_TranscriptLabel"), state.Transcript,
                    "native.resources.form.transcript", readOnly, false,
                    Localize("ResourceAdd_TranscriptPlaceholder"), palette, lines: 5),
                ResourceField(resources, NativeResourceField.Translation,
                    Localize("ResourceAdd_TranslationLabel"), state.Translation,
                    "native.resources.form.translation", readOnly, false,
                    Localize("ResourceAdd_TranslationPlaceholder"), palette, lines: 5)));
        if (!creating)
        {
            fields.Add(Card(palette, "native.resources.form.transcript.card",
                ResourceField(resources, NativeResourceField.Transcript,
                    Localize("ResourceEdit_Transcript"), state.Transcript,
                    "native.resources.form.transcript", readOnly, state.IsSmartResource,
                    null, palette, lines: 6)));
            fields.Add(Card(palette, "native.resources.form.translation.card",
                ResourceField(resources, NativeResourceField.Translation,
                    Localize("ResourceEdit_Translation"), state.Translation,
                    "native.resources.form.translation", readOnly, state.IsSmartResource,
                    null, palette, lines: 6)));
        }

        fields.Add(Card(palette, "native.resources.form.vocabulary.card",
            ResourceVocabulary(resources, state, creating, busy, palette).ToArray()));
        if (creating)
            fields.Add(Action(Localize("ResourceAdd_SaveResource"),
                "native.resources.add.save.bottom", resources.Save,
                !busy && state.IsEditable && state.CanSave));
        if (state.HasDraft && (state.Phase is NativeResourceDetailPhase.Conflict
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly
            || state.ErrorKey is "NativeResources_ReloadError" or "NativeResources_SaveInvalid"))
            fields.Add(Action(Localize("NativeResources_ReloadDiscard"),
                NativeAccess.ResourceReload, resources.ReloadDraft, !busy, primary: false));
        return page;
    }

    private ComposableNode ResourceFormAppBar(AndroidResourcesCoordinator resources,
        AndroidResourceFormState state, string title, bool creating, bool busy,
        AndroidVisualPalette palette)
    {
        var back = new IconButton(() => resources.CloseForm(),
            enabled: !resources.IsNavigationBlocked)
        {
            Modifier = Modifier.TestTag(NativeAccess.ResourceCancel)
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Back"))),
        };
        back.Add(new Icon(Icons.AutoMirrored.Default.ArrowBack, Localize("Common_Back"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var heading = Heading(title, palette, 20,
            Modifier.Semantics(semantics => semantics.Heading()));
        heading.MaxLines = 1;
        var bar = new TopAppBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.resources.form.appbar"),
            Title = heading,
            NavigationIcon = back,
        };
        if (!state.IsEditable)
            return bar;
        var canSave = !busy && state.CanSave;
        var save = new TextButton(resources.Save, enabled: canSave)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(NativeAccess.ResourceSave)
                .Semantics(semantics => semantics.ContentDescription(Localize("Common_Save"))),
        };
        save.Add(new Text(Localize("Common_Save")));
        var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            save,
        };
        if (!creating && !state.IsSmartResource)
        {
            var more = new IconButton(() => ToggleResourceMenu(ResourceMenu.FormMore),
                enabled: !busy)
            {
                Modifier = Modifier.TestTag("native.resources.form.more")
                    .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
            };
            more.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Text,
            });
            var menu = PopupMenu(expanded: _resourceMenu == ResourceMenu.FormMore,
                onDismissRequest: CloseResourceMenu);
            menu.Add(new DropdownMenuItem(text: new Text(Localize("Common_Delete")), onClick: () =>
            {
                CloseResourceMenu();
                resources.RequestDelete();
            })
            {
                Modifier = Modifier.TestTag("native.resources.form.delete.menu"),
            });
            actions.Add(new Box { more, menu });
        }
        bar.Actions = actions;
        return bar;
    }

    private ComposableNode ResourceDeleteDialog(
        AndroidResourcesCoordinator resources, AndroidResourceFormState state)
    {
        var pending = state.Phase == NativeResourceDetailPhase.Deleting;
        var confirm = new TextButton(resources.ConfirmDelete, enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.resources.edit.delete.confirm"),
        };
        confirm.Add(new Text(Localize("Common_Delete")));
        var cancel = new TextButton(resources.CancelDelete, enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.resources.edit.delete.cancel"),
        };
        cancel.Add(new Text(Localize("Common_Cancel")));
        return new AndroidX.Compose.AlertDialog(() =>
        {
            if (!pending)
                resources.CancelDelete();
        })
        {
            Modifier = Modifier.TestTag("native.resources.edit.delete"),
            Title = new Text(Localize("ResourceEdit_ConfirmDelete")),
            Text = new Text(Localize("ResourceEdit_DeleteMessage")),
            ConfirmButton = confirm,
            DismissButton = cancel,
        };
    }

    private ComposableNode ResourceField(AndroidResourcesCoordinator resources,
        NativeResourceField field, string label, string? value, string tag,
        bool disabled, bool readOnlyDisplay, string? placeholder,
        AndroidVisualPalette palette, int lines = 1)
    {
        if (readOnlyDisplay)
            return LabeledValue(label, value ?? string.Empty, tag, palette);
        var input = new OutlinedTextField(value ?? string.Empty,
            text => resources.ChangeDraft(field, text), enabled: !disabled,
            singleLine: lines == 1, minLines: lines)
        {
            Modifier = Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Label = new Text(label),
        };
        if (placeholder is not null)
            input.Placeholder = new Text(placeholder);
        return input;
    }

    private static ComposableNode LabeledValue(
        string label, string value, string tag, AndroidVisualPalette palette) =>
        new Column(Arrangement.SpacedBy(2))
        {
            Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription($"{label}: {value}")),
            Body(label, palette, secondary: true),
            Body(value, palette),
        };

    private ComposableNode ResourceSelect(AndroidResourcesCoordinator resources,
        ResourceMenu menu, NativeResourceField field, string label, string? current,
        IReadOnlyList<string> catalog, string tag, bool disabled, bool readOnlyDisplay,
        AndroidVisualPalette palette)
    {
        if (readOnlyDisplay)
            return LabeledValue(label, current ?? string.Empty, tag, palette);
        // A stored value outside the catalog stays selectable (same rule as iOS).
        var options = catalog.ToList();
        if (!string.IsNullOrWhiteSpace(current)
            && !options.Contains(current, StringComparer.OrdinalIgnoreCase))
            options.Insert(0, current);
        var selectedIndex = string.IsNullOrWhiteSpace(current) ? 0
            : options.FindIndex(value => string.Equals(value, current,
                StringComparison.OrdinalIgnoreCase));
        var selected = options[Math.Max(0, selectedIndex)];
        var anchor = new OutlinedButton(() => ToggleResourceMenu(menu), enabled: !disabled)
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag(tag)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{label}: {selected}");
                    semantics.StateDescription(selected);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        anchor.Add(new Row(Arrangement.SpaceBetween, Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            new Text(selected) { Color = palette.Text },
            new Icon(Icons.Default.ArrowDropDown, label) { Modifier = Modifier.Size(20), Tint = palette.Text },
        });
        var dropdown = PopupMenu(expanded: _resourceMenu == menu,
            onDismissRequest: CloseResourceMenu);
        for (var index = 0; index < options.Count; index++)
        {
            var value = options[index];
            var isSelected = index == selectedIndex;
            dropdown.Add(new DropdownMenuItem(text: new Text(value), onClick: () =>
            {
                CloseResourceMenu();
                resources.ChangeDraft(field, value);
            })
            {
                Modifier = Modifier.TestTag($"{tag}.option.{index}").Semantics(semantics =>
                {
                    semantics.ContentDescription(value);
                    semantics.Selected(isSelected);
                }),
                TrailingIcon = isSelected
                    ? new Icon(Icons.Default.Check, value) { Modifier = Modifier.Size(20), Tint = palette.Text }
                    : null,
            });
        }
        return new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth(),
            Body(label, palette, secondary: true),
            new Box { Modifier.FillMaxWidth(), anchor, dropdown },
        };
    }

    private List<ComposableNode> ResourceVocabulary(AndroidResourcesCoordinator resources,
        AndroidResourceFormState state, bool creating, bool busy, AndroidVisualPalette palette)
    {
        var items = new List<ComposableNode>
        {
            Heading(creating ? Localize("ResourceAdd_VocabularySection")
                    : string.Format(CultureInfo.CurrentCulture,
                        Localize("ResourceEdit_VocabularySection"), state.Vocabulary.Count),
                palette, 18, Modifier.TestTag("native.resources.form.vocabulary")
                    .Semantics(semantics => semantics.Heading())),
        };
        if (!creating && !state.IsSmartResource && !string.IsNullOrWhiteSpace(state.Transcript))
        {
            if (state.IsGenerating)
                items.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().TestTag("native.resources.edit.generate.progress"),
                    new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                    Body(Localize("ResourceEdit_Generating"), palette, secondary: true),
                });
            items.Add(Action(Localize(state.IsGenerating ? "ResourceEdit_Generating"
                    : "ResourceEdit_GenerateFromTranscript"),
                "native.resources.edit.generate", resources.Generate, !busy, primary: false));
        }
        if (!creating && !state.IsSmartResource)
        {
            items.Add(QuickAdd(resources, state, busy, palette));
            items.Add(Action(Localize("ResourceEdit_BulkImportToggle"),
                "native.resources.edit.bulk.toggle", resources.ToggleBulk, !busy,
                primary: false));
        }
        if (creating || !state.IsSmartResource && resources.BulkExpanded)
            AddBulkImport(items, resources, creating, busy, palette);

        if (state.Vocabulary.Count == 0 && !creating)
            items.Add(Body(Localize("ResourceEdit_NoVocabYet"), palette, secondary: true,
                modifier: Modifier.TestTag("native.resources.form.vocabulary.empty")));
        else if (creating && state.Vocabulary.Count > 0)
        {
            var table = new Column(Arrangement.SpacedBy(8))
            {
                Modifier.FillMaxWidth().TestTag("native.resources.add.table"),
            };
            foreach (var word in state.Vocabulary)
                table.Add(StagedWordRow(resources, word, busy, palette));
            items.Add(table);
            var count = Body(string.Format(CultureInfo.CurrentCulture,
                    Localize("ResourceAdd_VocabWordsAdded"), state.Vocabulary.Count),
                palette, secondary: true,
                modifier: Modifier.TestTag("native.resources.form.vocabulary.staged.count"));
            count.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
            items.Add(count);
        }
        else if (!creating)
        {
            var rows = new Column
            {
                Modifier.FillMaxWidth().TestTag("native.resources.form.vocabulary.rows"),
            };
            var first = true;
            foreach (var word in state.Vocabulary)
            {
                if (!first)
                    rows.Add(new HorizontalDivider { Color = palette.Border });
                first = false;
                rows.Add(EditWordRow(resources, word, state.IsSmartResource, busy, palette));
            }
            items.Add(rows);
        }
        return items;
    }

    private ComposableNode EditWordRow(AndroidResourcesCoordinator resources,
        AndroidResourceWord word, bool smart, bool busy, AndroidVisualPalette palette)
    {
        var target = Heading(word.TargetTerm, palette, 16,
            Modifier.TestTag($"native.resources.form.vocabulary.target.{word.Id}"));
        var native = Body(word.NativeTerm ?? string.Empty, palette, secondary: true,
            modifier: Modifier.TestTag($"native.resources.form.vocabulary.native.{word.Id}"));
        var row = new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag($"native.resources.form.vocabulary.word.{word.Id}")
                .Semantics(semantics => semantics.ContentDescription(
                    string.IsNullOrEmpty(word.NativeTerm)
                        ? word.TargetTerm : $"{word.TargetTerm}, {word.NativeTerm}")),
            new Column(Arrangement.SpacedBy(2)) { Modifier.Weight(1f), target, native },
        };
        if (!smart)
            row.Add(RemoveWordButton(resources, word.Id, busy, palette));
        return row;
    }

    private ComposableNode RemoveWordButton(AndroidResourcesCoordinator resources,
        string id, bool busy, AndroidVisualPalette palette)
    {
        var remove = new IconButton(() => resources.RemoveWord(id), enabled: !busy)
        {
            Modifier = Modifier.TestTag($"native.resources.form.vocabulary.remove.{id}")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("ResourceEdit_RemoveWord"))),
        };
        remove.Add(new Icon(Icons.Default.Close, Localize("ResourceEdit_RemoveWord"))
        {
            Modifier = Modifier.Size(20),
            Tint = resources.IsCreating ? palette.Danger : palette.SecondaryText,
        });
        return remove;
    }

    private ComposableNode StagedWordRow(AndroidResourcesCoordinator resources,
        AndroidResourceWord word, bool busy, AndroidVisualPalette palette)
    {
        var kinds = new[]
        {
            (LexicalUnitType.Word, Localize("Vocabulary_FilterTypeWord")),
            (LexicalUnitType.Phrase, Localize("Vocabulary_FilterTypePhrase")),
            (LexicalUnitType.Sentence, Localize("Vocabulary_FilterTypeSentence")),
        };
        var current = kinds.FirstOrDefault(kind => kind.Item1 == word.LexicalUnitType);
        if (current.Item2 is null)
            current = kinds[0];
        var typeLabel = Localize("Import_ColType");
        var anchor = new OutlinedButton(
            () => ToggleResourceMenu(ResourceMenu.WordKind, word.Id), enabled: !busy)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag($"native.resources.form.vocabulary.kind.{word.Id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription($"{typeLabel}: {current.Item2}");
                    semantics.StateDescription(current.Item2);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        anchor.Add(new Text(current.Item2));
        var menu = PopupMenu(
            expanded: _resourceMenu == ResourceMenu.WordKind && _resourceMenuWordId == word.Id,
            onDismissRequest: CloseResourceMenu);
        for (var index = 0; index < kinds.Length; index++)
        {
            var kind = kinds[index];
            var isSelected = kind.Item1 == current.Item1;
            menu.Add(new DropdownMenuItem(text: new Text(kind.Item2), onClick: () =>
            {
                CloseResourceMenu();
                resources.SetWordType(word.Id, kind.Item1);
            })
            {
                Modifier = Modifier.TestTag(
                    $"native.resources.form.vocabulary.kind.{word.Id}.option.{index}"),
                TrailingIcon = isSelected
                    ? new Icon(Icons.Default.Check, kind.Item2) { Modifier = Modifier.Size(20), Tint = palette.Text }
                    : null,
            });
        }
        var tags = Body(string.IsNullOrWhiteSpace(word.Tags) ? "—" : word.Tags,
            palette, secondary: true,
            modifier: Modifier.Weight(1f)
                .TestTag($"native.resources.form.vocabulary.tags.{word.Id}"));
        tags.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag($"native.resources.form.vocabulary.word.{word.Id}")
                .Border(1, palette.Border, AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Elevated,
        };
        surface.Add(new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(10),
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Column(Arrangement.SpacedBy(2))
                {
                    Modifier.Weight(1f),
                    Heading(word.TargetTerm, palette, 16, Modifier.TestTag(
                        $"native.resources.form.vocabulary.target.{word.Id}")),
                    Body(word.NativeTerm ?? string.Empty, palette, secondary: true,
                        modifier: Modifier.TestTag(
                            $"native.resources.form.vocabulary.native.{word.Id}")),
                },
                RemoveWordButton(resources, word.Id, busy, palette),
            },
            new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Box { anchor, menu },
                tags,
            },
        });
        return surface;
    }

    private ComposableNode QuickAdd(AndroidResourcesCoordinator resources,
        AndroidResourceFormState state, bool busy, AndroidVisualPalette palette)
    {
        var label = Localize("ResourceEdit_QuickAddLabel");
        var keyboard = KeyboardOptionsCompanion.Default;
        var done = keyboard.Copy(keyboard.Capitalization, keyboard.AutoCorrectEnabled,
            keyboard.KeyboardType, AndroidX.Compose.ImeAction.Done,
            keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus, keyboard.HintLocales);
        var column = new Column(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth().TestTag("native.resources.edit.quick.add"),
            new OutlinedTextField(resources.QuickQuery, resources.QuickQueryChanged,
                enabled: state.CanQuickAdd && !busy, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag("native.resources.edit.quick.search")
                    .Semantics(semantics => semantics.ContentDescription(label)),
                Label = new Text(label),
                Placeholder = new Text(Localize("ResourceEdit_QuickAddPlaceholder")),
                KeyboardOptions = done,
                KeyboardActions = KeyboardActionsHelper.Create(
                    onDone: resources.SubmitQuickQuery),
            },
        };
        if (!state.CanQuickAdd && state.Phase != NativeResourceDetailPhase.Conflict)
        {
            column.Add(Body(Localize(string.IsNullOrWhiteSpace(state.Language)
                    || string.Equals(state.Language, "Other", StringComparison.OrdinalIgnoreCase)
                    ? "ResourceEdit_SetLanguageFirst"
                    : "NativeResources_SaveLanguageBeforeQuickAdd"), palette, secondary: true,
                modifier: Modifier.TestTag("native.resources.edit.quick.hint")));
            return column;
        }
        if (string.IsNullOrWhiteSpace(resources.QuickQuery))
            return column;
        if (resources.CreateWordExpanded)
        {
            column.Add(new OutlinedTextField(resources.QuickNativeTerm,
                resources.QuickNativeTermChanged, enabled: !busy, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth()
                    .TestTag("native.resources.edit.quick.native")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize("ResourceEdit_NativeTermPrompt"))),
                Label = new Text(Localize("ResourceEdit_NativeTermPrompt")),
            });
            column.Add(Action(label, "native.resources.edit.quick.create",
                resources.CreateWord, !busy));
            return column;
        }
        if (!resources.ShowQuickSuggestions)
            return column;
        var listbox = new Column
        {
            Modifier.FillMaxWidth().TestTag("native.resources.edit.quick.listbox")
                .Border(1, palette.Border, AndroidVisualTheme.ControlRadius),
        };
        if (state.IsQuickAddSearching)
            listbox.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(24).Padding(4)
                    .TestTag("native.resources.edit.quick.loading")
            });
        else if (state.QuickAddResults.Count == 0)
        {
            listbox.Add(Body(Localize("ResourceEdit_NoMatches"), palette, secondary: true,
                modifier: Modifier.Padding(10)));
            var addNew = string.Format(CultureInfo.CurrentCulture,
                Localize("ResourceEdit_AddNewWord"), resources.QuickQuery.Trim());
            listbox.Add(QuickOption(addNew, null, "native.resources.edit.quick.new",
                resources.ExpandCreateWord, busy, palette));
        }
        else
            foreach (var candidate in state.QuickAddResults)
            {
                var id = candidate.Id;
                listbox.Add(QuickOption(candidate.TargetTerm, candidate.NativeTerm,
                    $"native.resources.edit.quick.option.{id}",
                    () => resources.AttachWord(id), busy, palette));
            }
        column.Add(listbox);
        return column;
    }

    private static ComposableNode QuickOption(string primary, string? secondary, string tag,
        Action select, bool busy, AndroidVisualPalette palette)
    {
        var label = string.IsNullOrWhiteSpace(secondary) ? primary : $"{primary}, {secondary}";
        var modifier = Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(tag);
        modifier = busy
            ? modifier.Semantics(semantics => semantics.ContentDescription(label))
            : modifier.Clickable(select).Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Role(SemanticsRole.Button);
            });
        var column = new Column(Arrangement.SpacedBy(2))
        {
            modifier.Padding(horizontal: 12, vertical: 8),
            Heading(primary, palette, 16),
        };
        if (!string.IsNullOrWhiteSpace(secondary))
            column.Add(Body(secondary, palette, secondary: true));
        return column;
    }

    private void AddBulkImport(List<ComposableNode> items,
        AndroidResourcesCoordinator resources, bool creating, bool busy,
        AndroidVisualPalette palette)
    {
        var pasteLabel = Localize(creating
            ? "ResourceAdd_PasteVocabLabel" : "ResourceEdit_ImportVocabLabel");
        items.Add(new OutlinedTextField(resources.PasteText, resources.PasteTextChanged,
            enabled: !busy, minLines: creating ? 6 : 4)
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag("native.resources.form.vocabulary.paste")
                .Semantics(semantics => semantics.ContentDescription(pasteLabel)),
            Label = new Text(pasteLabel),
            Placeholder = new Text(Localize(creating
                ? "ResourceAdd_VocabPlaceholder" : "ResourceEdit_VocabPlaceholder")),
        });
        var delimiters = new SingleChoiceSegmentedButtonRow
        {
            Modifier = Modifier.FillMaxWidth()
                .TestTag("native.resources.form.vocabulary.delimiter"),
        };
        foreach (var (value, key) in new[]
        {
            (AndroidResourcesCoordinator.Comma,
                creating ? "ResourceAdd_CommaDelimiter" : "ResourceEdit_Comma"),
            (AndroidResourcesCoordinator.Tab,
                creating ? "ResourceAdd_TabDelimiter" : "ResourceEdit_Tab"),
        })
        {
            var label = Localize(key);
            var selected = resources.Delimiter == value;
            var segment = new SegmentedButton(selected: selected,
                onClick: () => resources.DelimiterChanged(value))
            {
                Modifier = Modifier
                    .TestTag($"native.resources.form.vocabulary.delimiter.{value}")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(label);
                        semantics.Selected(selected);
                    }),
            };
            segment.Add(new Text(label));
            delimiters.Add(segment);
        }
        items.Add(delimiters);
        items.Add(Action(Localize(creating
                ? "ResourceAdd_ImportVocabulary" : "ResourceEdit_Import"),
            "native.resources.form.vocabulary.import", resources.ImportText,
            !busy && !string.IsNullOrWhiteSpace(resources.PasteText), primary: false));
        if (resources.FormState.IsImporting)
            items.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.resources.form.vocabulary.import.progress"),
                new CircularProgressIndicator { Modifier = Modifier.Size(20) },
                Body(Localize(creating ? "ResourceAdd_Importing" : "ResourceEdit_Importing"),
                    palette, secondary: true),
            });
        // The facade has no Compose document picker; file import stays visibly
        // unavailable rather than falling back to a non-Compose picker.
        items.Add(Body(Localize(creating
            ? "ResourceAdd_OrImportFile" : "ResourceEdit_OrImportFile"), palette, secondary: true));
        items.Add(Action(Localize(creating ? "ResourceAdd_ChooseFile" : "ResourceEdit_ChooseFile"),
            "native.resources.form.vocabulary.file", () => { }, enabled: false, primary: false));
        items.Add(Body(Localize("NativeResources_Unavailable"), palette, secondary: true,
            modifier: Modifier.TestTag("native.resources.form.vocabulary.file.unavailable")));
    }

    private static string FormatResourceError(string key) =>
        key is "ResourceEdit_DeleteFailed" or "FailedToLoadVocabularyData"
            ? string.Format(CultureInfo.CurrentCulture, Localize(key), Localize("Common_Error"))
            : Localize(key);
}
