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

        var toggleLabel = Localize(choose.ShowAllActivities
            ? "Dashboard_ShowLess" : "Dashboard_SeeAll");
        var toggle = new TextButton(ToggleActivityCatalog, enabled: !Busy.Value)
        {
            Modifier = Modifier.HeightIn(min: AndroidVisualTheme.ControlHeight)
                .TestTag("native.dashboard.choose.activities.toggle")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(toggleLabel);
                    semantics.StateDescription(toggleLabel);
                }),
        };
        toggle.Add(new Text(toggleLabel));
        section.Add(new Row(Arrangement.SpaceBetween,
            Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(Localize("Dashboard_Activities"), palette, 18),
            toggle
        });

        var visibleCount = choose.ShowAllActivities
            ? NativeDashboardPresenter.ChooseOwnActivities.Length : 6;
        foreach (var activity in NativeDashboardPresenter.ChooseOwnActivities
            .Take(visibleCount))
        {
            var activityName = Localize(activity.LabelKey);
            var unavailable = Localize("NativeResources_Unavailable");
            section.Add(new Column(Arrangement.SpacedBy(2))
            {
                Modifier.FillMaxWidth().Padding(vertical: 4)
                    .TestTag($"native.dashboard.choose.activity.{activity.Route}.unavailable")
                    .Semantics(semantics =>
                        semantics.ContentDescription($"{activityName}. {unavailable}")),
                Heading(activityName, palette, 16),
                Body(unavailable, palette, secondary: true)
            });
        }
        return section;
    }

    private ComposableNode RenderChooseSelector(AndroidVisualPalette palette)
    {
        var choose = Dashboard.ChooseOwn;
        var sheet = new ModalBottomSheet(
            onDismissRequest: () => Publish(() => _chooseSelector.Value = (int)ChooseSelector.None))
        {
            Modifier = Modifier.TestTag("native.dashboard.choose.selector")
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
