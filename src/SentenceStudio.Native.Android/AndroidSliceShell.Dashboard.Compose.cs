using System.Collections.Immutable;
using AndroidX.Compose;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private enum ChooseSelector { None, Resources, Skills }

    private readonly MutableState<int> _chooseSelector =
        new((int)ChooseSelector.None);

    private ComposableNode RenderChooseOwn(AndroidVisualPalette palette)
    {
        var choose = Dashboard.ChooseOwn;
        if (choose is null)
            return Body(Localize("Common_Loading"), palette, secondary: true);

        var resources = choose.Resources.IsDefault ? [] : choose.Resources;
        var skills = choose.Skills.IsDefault ? [] : choose.Skills;
        var selectedIds = choose.SelectedResourceIds.IsDefault
            ? [] : choose.SelectedResourceIds;
        var selected = resources
            .Where(resource => selectedIds.Contains(resource.Id))
            .Select(resource => resource.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToArray();
        var skill = skills.FirstOrDefault(
            candidate => candidate.Id == choose.SelectedSkillId);
        var canSelect = choose.ErrorKey is null && !Busy.Value;
        var content = new List<ComposableNode>
        {
            Heading(Localize("Dashboard_ChooseMyOwn"), palette, 20)
        };
        if (choose.ErrorKey is { } error)
        {
            content.Add(StatusAlert(Localize(error), palette, error: true,
                automationId: "native.dashboard.choose.error"));
            content.Add(Action(Localize(NativeText.Retry),
                "native.dashboard.choose.retry", () => SelectDashboardMode(true),
                !Busy.Value, primary: false));
        }
        content.Add(Action($"{Localize("Dashboard_LearningResourcesLabel")} " +
                $"({selectedIds.Length})",
                "native.dashboard.choose.resources",
                () => Publish(() => _chooseSelector.Value = (int)ChooseSelector.Resources),
                canSelect, primary: false));
        content.Add(
            selected.Length > 0
                ? Body(string.Join(", ", selected), palette, secondary: true)
                : Body(Localize("Dashboard_LearningResourcesLabel"),
                    palette, secondary: true));
        content.Add(Action(skill?.Title is { Length: > 0 } title
                    ? $"{Localize("Dashboard_SkillProfileLabel")}: {title}"
                    : Localize("Dashboard_SkillProfileLabel"),
                "native.dashboard.choose.skill",
                () => Publish(() => _chooseSelector.Value = (int)ChooseSelector.Skills),
                canSelect, primary: false));
        var section = new Column(Arrangement.SpacedBy(10))
        {
            Modifier.FillMaxWidth().TestTag("native.dashboard.choose")
        };
        section.Add(Card(palette, "native.dashboard.choose.filters",
            content.ToArray()));

        // Source Index.razor:445-471: the See All toggle exists below md (768) only;
        // from md every tile shows. Tiles are col-6 / col-md-4 / col-lg-3 cards.
        var compact = WindowWidthDp < 768;
        var header = new Row(Arrangement.SpaceBetween,
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(Localize("Dashboard_Activities"), palette, 18,
                Modifier.Semantics(semantics => semantics.Heading())),
        };
        if (compact && NativeDashboardPresenter.ChooseOwnActivities.Length > 6)
        {
            var toggleLabel = Localize(choose.ShowAllActivities
                ? "Dashboard_ShowLess" : "Dashboard_SeeAll");
            var toggle = new OutlinedButton(ToggleActivityCatalog, enabled: !Busy.Value)
            {
                Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                    .TestTag("native.dashboard.choose.activities.toggle")
                    .Semantics(semantics =>
                    {
                        semantics.ContentDescription(toggleLabel);
                        semantics.StateDescription(toggleLabel);
                    }),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            toggle.Add(new Text(toggleLabel));
            header.Add(toggle);
        }
        section.Add(header);

        var visible = !compact || choose.ShowAllActivities
            ? NativeDashboardPresenter.ChooseOwnActivities
            : NativeDashboardPresenter.ChooseOwnActivities.Take(6).ToImmutableArray();
        var columns = WindowWidthDp >= 992 ? 4 : compact ? 2 : 3;
        for (var start = 0; start < visible.Length; start += columns)
        {
            var row = new Row(Arrangement.SpacedBy(12))
            {
                Modifier.FillMaxWidth(),
            };
            for (var index = start; index < start + columns; index++)
                row.Add(index < visible.Length
                    ? ActivityTile(visible[index], canSelect, palette)
                    : new Box { Modifier.Weight(1f) });
            section.Add(row);
        }
        return section;
    }

    /// <summary>One- and two-line labels share this height, so a row stays even (source h-100).</summary>
    private const int ActivityTileMinHeight = 116;

    /// <summary>
    /// One source activity card: icon over the label, the whole card launches.
    /// The Compose facade carries only the core Material icons, so each is the
    /// closest available stand-in for the source Bootstrap icon.
    /// </summary>
    private ComposableNode ActivityTile(
        NativeChooseOwnActivity activity, bool canSelect, AndroidVisualPalette palette)
    {
        var activityName = Localize(activity.LabelKey);
        var route = activity.Route;
        var implemented = route == "vocab-quiz"
            || NativeActivityCatalog.IsImplemented(route);
        var unavailable = Localize("NativeResources_Unavailable");
        var modifier = Modifier.Weight(1f).HeightIn(min: ActivityTileMinHeight)
            .TestTag(implemented
                ? $"native.dashboard.choose.activity.{route}"
                : $"native.dashboard.choose.activity.{route}.unavailable");
        modifier = implemented && canSelect
            ? modifier.Clickable(route == "vocab-quiz"
                    ? OpenChooseOwnQuiz : () => OpenChooseOwnActivity(route))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(activityName);
                    semantics.Role(SemanticsRole.Button);
                })
            : modifier.Semantics(semantics => semantics.ContentDescription(
                implemented ? activityName : $"{activityName}. {unavailable}"));
        var label = Heading(activityName, palette, 16, Modifier.Padding(top: 6));
        label.Align = TextAlign.Center;
        var content = new Column(Arrangement.Center,
            Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.FillMaxWidth().HeightIn(min: ActivityTileMinHeight).Padding(12)
                .Alpha(implemented ? 1f : 0.55f),
            new Icon(ChooseOwnTileIcon(route), null)
            {
                Modifier = Modifier.Size(30),
                Tint = palette.Primary,
            },
            label,
        };
        if (!implemented)
        {
            var caption = Body(unavailable, palette, secondary: true);
            caption.Align = TextAlign.Center;
            content.Add(caption);
        }
        var tile = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        tile.Add(content);
        return tile;
    }

    private static AndroidX.Compose.UI.Graphics.Vector.ImageVector ChooseOwnTileIcon(
        string route) => route switch
    {
        "conversation" => Icons.Default.Face,
        "scene" => Icons.Default.AccountBox,
        "translation" => Icons.AutoMirrored.Default.ArrowForward,
        "cloze" => Icons.Default.Build,
        "writing" => Icons.Default.Create,
        "diary" => Icons.Default.DateRange,
        "reading" => Icons.Default.Info,
        "vocab-quiz" => Icons.Default.Check,
        "vocab-matching" => Icons.Default.Search,
        "shadowing" => Icons.Default.PlayArrow,
        "how-do-you-say" => Icons.AutoMirrored.Default.Send,
        "minimal-pairs" => Icons.Default.Call,
        "word-association" => Icons.Default.Star,
        "numberdrill" => Icons.Default.Add,
        _ => Icons.Default.Info,
    };

    private ComposableNode RenderChooseSelector(AndroidVisualPalette palette)
    {
        var choose = Dashboard.ChooseOwn;
        var sheet = new ModalBottomSheet(
            onDismissRequest: () => Publish(() => _chooseSelector.Value = (int)ChooseSelector.None))
        {
            Modifier = PopupWindowSemantics(Modifier.TestTag("native.dashboard.choose.selector"))
        };
        var content = new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding,
                    vertical: AndroidVisualTheme.CardPadding)
        };
        if (choose?.ErrorKey is { } error)
            content.Add(StatusAlert(Localize(error), palette, error: true,
                automationId: "native.dashboard.choose.selector.error"));
        if (_chooseSelector.Value == (int)ChooseSelector.Resources)
        {
            content.Add(Heading(Localize("Dashboard_LearningResourcesLabel"),
                palette, 20));
            content.Add(new LazyColumn<NativeChooseOwnOption>(
                choose?.Resources.IsDefault == false ? choose.Resources : [],
                resource => ResourceOption(resource, palette))
            {
                Key = resource => resource.Id,
                Modifier = Modifier.FillMaxWidth().HeightIn(max: 440)
                    .TestTag("native.dashboard.choose.resources.list")
            });
        }
        else
        {
            content.Add(Heading(Localize("Dashboard_SkillProfileLabel"),
                palette, 20));
            content.Add(Action(Localize("Common_Clear"),
                "native.dashboard.choose.skill.clear",
                () =>
                {
                    SelectChooseSkill(null);
                    Publish(() => _chooseSelector.Value = (int)ChooseSelector.None);
                }, choose is { ErrorKey: null } && !Busy.Value,
                primary: false));
            content.Add(new LazyColumn<NativeChooseOwnOption>(
                choose?.Skills.IsDefault == false ? choose.Skills : [],
                skill => SkillOption(skill, palette))
            {
                Key = skill => skill.Id,
                Modifier = Modifier.FillMaxWidth().HeightIn(max: 440)
                    .TestTag("native.dashboard.choose.skills.list")
            });
        }
        content.Add(Action(Localize("Common_Close"),
            "native.dashboard.choose.selector.close",
            () => Publish(() => _chooseSelector.Value = (int)ChooseSelector.None),
            !Busy.Value, primary: false));
        sheet.Add(content);
        return sheet;
    }

    private ComposableNode ResourceOption(
        NativeChooseOwnOption resource, AndroidVisualPalette palette)
    {
        var label = resource.Title;
        var selected = Dashboard.ChooseOwn?.SelectedResourceIds.Contains(resource.Id)
            == true;
        return new Row(Arrangement.SpacedBy(8),
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth().HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag($"native.dashboard.choose.resource.{resource.Id}"),
            new Checkbox(@checked: selected,
                onCheckedChange: _ => ToggleChooseResource(resource.Id),
                enabled: Dashboard.ChooseOwn is { ErrorKey: null }
                    && !Busy.Value)
            {
                Modifier = Modifier.Semantics(semantics =>
                    semantics.ContentDescription(label))
            },
            Body(label, palette)
        };
    }

    private ComposableNode SkillOption(
        NativeChooseOwnOption skill, AndroidVisualPalette palette)
    {
        var selected = Dashboard.ChooseOwn?.SelectedSkillId == skill.Id;
        var label = skill.Title;
        void Choose()
        {
            SelectChooseSkill(skill.Id);
            Publish(() => _chooseSelector.Value = (int)ChooseSelector.None);
        }
        var modifier = Modifier.FillMaxWidth()
                .HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag($"native.dashboard.choose.skill.{skill.Id}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(selected);
                });
        if (selected)
        {
            var button = new AndroidX.Compose.Button(Choose,
                enabled: Dashboard.ChooseOwn is { ErrorKey: null }
                    && !Busy.Value)
            {
                Modifier = modifier
            };
            button.Add(new Text(label));
            return button;
        }
        var outlined = new OutlinedButton(Choose,
            enabled: Dashboard.ChooseOwn is { ErrorKey: null }
                && !Busy.Value)
        {
            Modifier = modifier
        };
        outlined.Add(Body(label, palette));
        return outlined;
    }
}
