using System.Globalization;
using AndroidX.Compose;
using SentenceStudio.Native.Features.Skills;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Skills (source Skills.razor, SkillAdd.razor, SkillEdit.razor; iOS SkillsView and
/// SkillProfileFormView) on the shared owner-scoped SkillsCoordinator: a lazy grid
/// of skill profiles, add and edit forms, and a cancelable delete confirmation.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private ComposableNode RenderSkills(AndroidVisualPalette palette)
    {
        if (_skills is { IsFormOpen: true } form)
            return new Composed(composer =>
            {
                var scroll = composer.Remember(() => new AndroidX.Compose.ScrollState(),
                    form.FormState.Creating);
                return SkillFormPage(form, palette, scroll);
            });
        return SkillsListPage(_skills, palette);
    }

    private ComposableNode SkillsListPage(SkillsCoordinator? skills, AndroidVisualPalette palette)
    {
        var state = skills?.ListState
            ?? new SkillsListState(SkillsListPhase.Blocked, [], NativeText.InvalidCredentials);
        var canAdd = skills is not null && !skills.IsPending
            && state.Phase is SkillsListPhase.Empty or SkillsListPhase.Populated;
        var add = new IconButton(() => SkillsCommand(s => s.OpenCreate()), enabled: canAdd)
        {
            Modifier = Modifier.TestTag("native.skills.add")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("Skills_AddSkillProfile"))),
        };
        add.Add(new Icon(Icons.Default.Add, Localize("Skills_AddSkillProfile"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var header = ScreenHeader(Localize("Skills_Title"), palette, back: true,
            backAutomationId: "native.skills.back");
        if (header is TopAppBar bar)
            bar.Actions = add;
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.skills.screen"),
            header,
        };
        var top = new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth().Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8),
        };
        if (state.FeedbackKey is { } feedback)
            top.Add(StatusAlert(Localize(feedback), palette, success: true,
                automationId: "native.skills.feedback"));
        switch (state.Phase)
        {
            case SkillsListPhase.Loading:
                top.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
                {
                    Modifier.FillMaxWidth().TestTag("native.skills.loading"),
                    new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                    Body(Localize("Common_Loading"), palette, secondary: true),
                });
                break;
            case SkillsListPhase.Blocked:
            case SkillsListPhase.LoadError:
                top.Add(StatusAlert(SkillError(state.ErrorKey), palette, error: true,
                    automationId: "native.skills.error"));
                if (state.Phase == SkillsListPhase.LoadError && skills is not null)
                    top.Add(Action(Localize(NativeText.Retry), "native.skills.retry",
                        () => SkillsCommand(s => _ = s.ReloadAsync()), true, primary: false));
                break;
            case SkillsListPhase.Empty:
                var message = Body(Localize("Skills_NoProfilesYet"), palette, secondary: true,
                    modifier: Modifier.FillMaxWidth());
                message.Align = TextAlign.Center;
                top.Add(new Column(Arrangement.SpacedBy(16), Alignment.Horizontal.CenterHorizontally)
                {
                    Modifier.FillMaxWidth().Padding(32).TestTag("native.skills.empty"),
                    message,
                    Action(Localize("Skills_AddFirstProfile"), "native.skills.empty.add",
                        () => SkillsCommand(s => s.OpenCreate()), canAdd),
                });
                break;
        }
        page.Add(top);
        if (state.Phase == SkillsListPhase.Populated)
        {
            var columns = WindowWidthDp < 768 ? 1 : WindowWidthDp < 992 ? 2 : 3;
            page.Add(new LazyVerticalGrid<SkillProfile>(GridCells.Fixed(columns),
                state.Profiles.ToArray(), profile => SkillCard(profile, palette))
            {
                Key = profile => profile.Id,
                Modifier = Modifier.FillMaxWidth().Weight(1f)
                    .Padding(horizontal: AndroidVisualTheme.PagePadding)
                    .TestTag("native.skills.grid"),
                VerticalArrangement = Arrangement.SpacedBy(12),
                HorizontalArrangement = Arrangement.SpacedBy(12),
            });
        }
        return page;
    }

    private ComposableNode SkillCard(SkillProfile profile, AndroidVisualPalette palette)
    {
        var title = profile.Title ?? string.Empty;
        var id = profile.Id;
        var content = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().HeightIn(min: 120).Padding(AndroidVisualTheme.CardPadding),
            Heading(title, palette, 20, Modifier.TestTag($"native.skills.item.{id}.title")),
        };
        if (!string.IsNullOrEmpty(profile.Description))
            content.Add(new Text(profile.Description)
            {
                Modifier = Modifier.TestTag($"native.skills.item.{id}.description"),
                Color = palette.SecondaryText,
                FontSize = new Sp(14),
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            });
        var card = new Surface
        {
            Modifier = Modifier.FillMaxWidth()
                .Clip(Shape.RoundedCorners(AndroidVisualTheme.CardRadius))
                .Clickable(() => SkillsCommand(s => _ = s.OpenEditAsync(id)))
                .TestTag($"native.skills.item.{id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(title);
                    semantics.Role(SemanticsRole.Button);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        card.Add(content);
        return card;
    }

    private ComposableNode SkillFormPage(SkillsCoordinator skills, AndroidVisualPalette palette,
        AndroidX.Compose.ScrollState scroll)
    {
        var state = skills.FormState;
        var busy = state.Phase is SkillFormPhase.Saving or SkillFormPhase.Deleting;
        var editable = state.Phase is SkillFormPhase.Creating or SkillFormPhase.Editing
            or SkillFormPhase.SaveError or SkillFormPhase.DeleteError
            or SkillFormPhase.Saving or SkillFormPhase.Deleting;
        var header = ScreenHeader(Localize(state.Creating ? "SkillAdd_Title" : "SkillEdit_Title"),
            palette, back: true, backAutomationId: "native.skills.form.back");
        if (editable && header is TopAppBar bar)
        {
            var save = new TextButton(() => SkillsCommand(s => _ = s.SaveAsync()), enabled: !busy)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.skills.form.save.primary")
                    .Semantics(semantics => semantics.ContentDescription(Localize("Common_Save"))),
            };
            save.Add(new Text(Localize("Common_Save")));
            var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically) { save };
            if (!state.Creating)
            {
                var more = new IconButton(() => ToggleMoreMenu("skills.form.more"), enabled: !busy)
                {
                    Modifier = Modifier.TestTag("native.skills.form.more")
                        .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
                };
                more.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
                {
                    Modifier = Modifier.Size(24),
                    Tint = palette.Text,
                });
                var menu = PopupMenu(expanded: _moreMenu == "skills.form.more",
                    onDismissRequest: CloseMoreMenu);
                menu.Add(new DropdownMenuItem(text: new Text(Localize("Common_Delete")), onClick: () =>
                {
                    CloseMoreMenu();
                    SkillsCommand(s => s.RequestDelete());
                })
                {
                    Modifier = Modifier.TestTag("native.skills.edit.delete.request"),
                });
                actions.Add(new Box { more, menu });
            }
            bar.Actions = actions;
        }
        var fields = new Column(Arrangement.SpacedBy(16))
        {
            Modifier = Modifier.FillMaxSize().VerticalScroll(scroll)
                .Padding(AndroidVisualTheme.PagePadding)
                .TestTag("native.skills.form.scroll"),
        };
        var page = new Column
        {
            Modifier.FillMaxSize().TestTag(state.Creating
                ? "native.skills.add.screen" : "native.skills.edit.screen"),
            header,
            fields,
        };
        if (state.Phase is SkillFormPhase.Loading or SkillFormPhase.Saving or SkillFormPhase.Deleting)
            fields.Add(new Row(Arrangement.SpacedBy(10), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth().TestTag("native.skills.form.progress"),
                new CircularProgressIndicator { Modifier = Modifier.Size(24) },
                Body(Localize(state.Phase switch
                {
                    SkillFormPhase.Loading => "Common_Loading",
                    SkillFormPhase.Deleting => "Common_Delete",
                    _ => "Common_Save",
                }), palette, secondary: true),
            });
        if (state.ErrorKey is { } error)
            fields.Add(StatusAlert(SkillError(error), palette, error: true,
                automationId: "native.skills.form.error"));
        if (editable)
        {
            var title = new OutlinedTextField(state.Title, value =>
            {
                skills.SetTitle(value);
                RenderMoreNow();
            }, enabled: !busy, singleLine: true)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.skills.form.title")
                    .Semantics(semantics => semantics.ContentDescription(Localize("SkillAdd_TitleLabel"))),
                Label = new Text(Localize("SkillAdd_TitleLabel")),
            };
            if (state.Creating)
                title.Placeholder = new Text(Localize("SkillAdd_TitlePlaceholder"));
            var description = new OutlinedTextField(state.Description, value =>
            {
                skills.SetDescription(value);
                RenderMoreNow();
            }, enabled: !busy, minLines: 6)
            {
                Modifier = Modifier.FillMaxWidth().TestTag("native.skills.form.description")
                    .Semantics(semantics => semantics.ContentDescription(
                        Localize("SkillAdd_DescriptionLabel"))),
                Label = new Text(Localize("SkillAdd_DescriptionLabel")),
            };
            if (state.Creating)
                description.Placeholder = new Text(Localize("SkillAdd_DescriptionPlaceholder"));
            fields.Add(Card(palette, "native.skills.form.fields", title, description));

            if (!state.Creating && state.CreatedAt is { } created && state.UpdatedAt is { } updated)
                fields.Add(Card(palette, "native.skills.form.details",
                    Heading(Localize("SkillEdit_DetailsTitle"), palette, 20,
                        Modifier.Semantics(semantics => semantics.Heading())),
                    SkillDate(Localize("SkillEdit_Created"),
                        AndroidResourceDates.DetailCreatedDate(created, isSmartResource: false),
                        "native.skills.form.created", palette),
                    SkillDate(Localize("SkillEdit_Updated"),
                        AndroidResourceDates.DetailUpdatedDate(updated),
                        "native.skills.form.updated", palette)));

            if (state.Creating)
                fields.Add(Action(Localize("SkillAdd_SaveSkillProfile"),
                    "native.skills.form.save.bottom", () => SkillsCommand(s => _ = s.SaveAsync()),
                    !busy));
        }
        else if (state.Phase == SkillFormPhase.LoadError)
            fields.Add(Action(Localize(NativeText.Retry), "native.skills.form.retry",
                () => SkillsCommand(s => _ = s.RetryEditAsync()), true, primary: false));
        fields.Add(Action(Localize("Common_Cancel"), "native.skills.form.cancel",
            () => SkillsCommand(s => s.CancelForm()), !busy, primary: false));
        if (state.ShowDeleteConfirmation)
            page.Add(SkillDeleteDialog(skills, state.Phase == SkillFormPhase.Deleting));
        return page;
    }

    private static ComposableNode SkillDate(string name, string value, string tag,
        AndroidVisualPalette palette)
    {
        var label = Strong(name, palette);
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription($"{name}: {value}")),
            label,
            Body(value, palette),
        };
    }

    private ComposableNode SkillDeleteDialog(SkillsCoordinator skills, bool pending)
    {
        var confirm = new TextButton(() => SkillsCommand(s => _ = s.ConfirmDeleteAsync()),
            enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.skills.edit.delete.confirm"),
        };
        confirm.Add(new Text(Localize("Common_Delete")));
        var cancel = new TextButton(() => SkillsCommand(s => s.CancelDelete()), enabled: !pending)
        {
            Modifier = Modifier.TestTag("native.skills.edit.delete.cancel"),
        };
        cancel.Add(new Text(Localize("Common_Cancel")));
        return new AndroidX.Compose.AlertDialog(() =>
        {
            if (!skills.IsPending)
                skills.CancelDelete();
        })
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag("native.skills.edit.delete")),
            Title = new Text(Localize("SkillEdit_ConfirmDelete")),
            Text = new Text(Localize("SkillEdit_DeleteMessage")),
            ConfirmButton = confirm,
            DismissButton = cancel,
        };
    }

    /// <summary>The Skills coordinator is not thread-safe (iOS main thread): commands run on the UI thread.</summary>
    private void SkillsCommand(Action<SkillsCoordinator> command)
    {
        if (_skills is { } skills && MorePage == MoreScreen.Skills)
            Post(() =>
            {
                if (ReferenceEquals(_skills, skills))
                    command(skills);
            });
    }

    private static string SkillError(string? errorKey)
    {
        var key = errorKey ?? NativeText.UnexpectedError;
        var message = Localize(key);
        return key is "SkillAdd_SaveFailed" or "SkillEdit_LoadFailed"
            or "SkillEdit_SaveFailed" or "SkillEdit_DeleteFailed"
            ? string.Format(CultureInfo.CurrentCulture, message,
                Localize(NativeText.UnexpectedError))
            : message;
    }
}
