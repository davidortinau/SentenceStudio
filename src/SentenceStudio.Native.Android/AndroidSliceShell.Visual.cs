using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using System.Globalization;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

internal sealed partial class AndroidSliceShell
{
    private static Text Heading(string value, AndroidVisualPalette palette,
        int size = 28, Modifier? modifier = null) => new(value)
    {
        Modifier = modifier,
        Color = palette.Text,
        FontSize = new Sp(size),
        FontWeight = FontWeight.SemiBold,
    };

    private static Text QuizPromptHeading(string value, AndroidVisualPalette palette) => new(value)
    {
        Modifier = Modifier.FillMaxWidth().Padding(vertical: 14)
            .TestTag(NativeAccess.QuizQuestion)
            .Semantics(semantics => semantics.ContentDescription(value)),
        Color = palette.Primary,
        FontSize = new Sp(AndroidVisualTheme.QuizPromptSize),
        FontWeight = FontWeight.Bold,
        Align = TextAlign.Center,
    };

    private static Text Body(string value, AndroidVisualPalette palette,
        bool secondary = false, Modifier? modifier = null) => new(value)
    {
        Modifier = modifier,
        Color = secondary ? palette.SecondaryText : palette.Text,
        FontSize = new Sp(15),
    };

    private static ComposableNode Card(AndroidVisualPalette palette,
        string? automationId, params ComposableNode[] children)
    {
        var modifier = Modifier.FillMaxWidth().Padding(bottom: 12);
        if (automationId is not null)
            modifier = modifier.TestTag(automationId);
        var surface = new Surface
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        var content = new Column(Arrangement.SpacedBy(8))
        {
            Modifier = Modifier.FillMaxWidth().Padding(AndroidVisualTheme.CardPadding),
        };
        foreach (var child in children)
            content.Add(child);
        surface.Add(content);
        return surface;
    }

    private static ComposableNode StatusAlert(string message,
        AndroidVisualPalette palette, bool error = false,
        string? automationId = null, bool success = false)
    {
        var modifier = Modifier.FillMaxWidth().Padding(bottom: 12);
        if (automationId is not null)
            modifier = modifier.TestTag(automationId);
        var surface = new Surface
        {
            Modifier = modifier.Border(1,
                error ? palette.Danger : success ? palette.Success : palette.Border,
                AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = error ? palette.DangerSurface : palette.Elevated,
        };
        surface.Add(Body(message, palette, modifier: Modifier.Padding(12)));
        return surface;
    }

    private static ComposableNode Action(string label, string automationId,
        Action command, bool enabled, bool primary = true)
    {
        var modifier = Modifier.FillMaxWidth()
            .HeightIn(min: AndroidVisualTheme.ControlHeight)
            .TestTag(automationId)
            .Semantics(semantics => semantics.ContentDescription(label));
        if (!primary)
        {
            var secondary = new OutlinedButton(onClick: command, enabled: enabled)
            {
                Modifier = modifier,
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            secondary.Add(new Text(label));
            return secondary;
        }
        var button = new ComposeButton(onClick: command, enabled: enabled)
        {
            Modifier = modifier,
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        button.Add(new Text(label));
        return button;
    }

    private ComposableNode ScreenHeader(string title, AndroidVisualPalette palette,
        bool back = false, bool refresh = false, string backAutomationId = "native.quiz.back")
    {
        var bar = new TopAppBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.screen.header"),
            Title = Heading(title, palette, 22,
                Modifier.Semantics(semantics => semantics.Heading()))
        };
        if (back)
        {
            var button = new IconButton(
                onClick: HandleBackAction, enabled: !Busy.Value)
            {
                Modifier = Modifier.TestTag(backAutomationId)
                    .Semantics(semantics => semantics.ContentDescription(Localize("Common_Back"))),
            };
            button.Add(new Icon(Icons.AutoMirrored.Default.ArrowBack,
                Localize("Common_Back"))
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Text
            });
            bar.NavigationIcon = button;
        }
        if (refresh)
        {
            var button = new IconButton(
                onClick: RefreshPlan, enabled: !Busy.Value)
            {
                Modifier = Modifier.TestTag("native.dashboard.refresh")
                    .Semantics(semantics => semantics.ContentDescription(Localize("Common_Refresh"))),
            };
            button.Add(new Icon(Icons.Default.Refresh, Localize("Common_Refresh"))
            {
                Modifier = Modifier.Size(24),
                Tint = palette.Text
            });
            bar.Actions = button;
        }
        return bar;
    }

    private ComposableNode DashboardAppBar(AndroidVisualPalette palette)
    {
        var refresh = new IconButton(RefreshPlan, enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag("native.dashboard.refresh")
                .Semantics(semantics => semantics.ContentDescription(
                    Localize("Common_Refresh"))),
        };
        refresh.Add(new Icon(Icons.Default.Refresh, Localize("Common_Refresh"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var overflow = new IconButton(
            () => Publish(() => _dashboardMenuOpen = true), enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag("native.dashboard.menu")
                .Semantics(semantics => semantics.ContentDescription(Localize("More"))),
        };
        overflow.Add(new Icon(Icons.Default.MoreVert, Localize("More"))
        {
            Modifier = Modifier.Size(24),
            Tint = palette.Text,
        });
        var menu = new DropdownMenu(
            expanded: _dashboardMenuOpen,
            onDismissRequest: () => Publish(() => _dashboardMenuOpen = false));
        if (Dashboard.Phase == NativePhase.Ready)
            menu.Add(new DropdownMenuItem(
                text: new Text(Localize(NativeText.Regenerate)),
                onClick: () =>
                {
                    Publish(() => _dashboardMenuOpen = false);
                    RegeneratePlan();
                })
            {
                Modifier = Modifier.TestTag("native.dashboard.menu.regenerate"),
            });
        menu.Add(new DropdownMenuItem(
            text: new Text(Localize("Settings_Title")),
            onClick: OpenAppearanceSettings)
        {
            Modifier = Modifier.TestTag("native.dashboard.menu.settings"),
        });
        var signOutLabel = Localize("Nav_Logout");
#if DEBUG
        if (_preview)
            signOutLabel = "Exit Debug preview";
#endif
        menu.Add(new DropdownMenuItem(
            text: new Text(signOutLabel),
            onClick: () =>
            {
                Publish(() => _dashboardMenuOpen = false);
                SignOut();
            })
        {
            Modifier = Modifier.TestTag("native.dashboard.menu.signout"),
        });
        var actions = new Row(Arrangement.SpacedBy(4),
            Alignment.Vertical.CenterVertically);
        if (!Dashboard.IsNewUser || _preview)
            actions.Add(refresh);
        actions.Add(new Box { overflow, menu });
        return new TopAppBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.dashboard.appbar"),
            Title = Heading(Localize(Dashboard.IsNewUser && !_preview
                    ? "Dashboard_Welcome" : "Dashboard_Title"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            Actions = actions,
        };
    }

    private static ComposableNode ProgressBar(float progress,
        AndroidVisualPalette palette, bool completed = false) =>
        new LinearProgressIndicator
        {
            Modifier = Modifier.FillMaxWidth().Height(8)
                .Clip(4),
            Progress = Math.Clamp(progress, 0f, 1f),
            Color = progress <= 0f
                ? completed ? palette.Elevated : palette.Border
                : completed ? palette.Success : palette.Primary,
            TrackColor = completed ? palette.Elevated : palette.Border,
        };

    private static ComposableNode SummaryStatistic(
        int value, string label, AndroidVisualPalette palette)
    {
        var number = Heading(value.ToString(CultureInfo.CurrentCulture),
            palette, 22, Modifier.FillMaxWidth());
        number.Align = TextAlign.Center;
        var caption = Body(label, palette, secondary: true,
            modifier: Modifier.FillMaxWidth());
        caption.FontSize = new Sp(AndroidVisualTheme.CaptionSize);
        caption.Align = TextAlign.Center;
        return new Column(Arrangement.SpacedBy(2),
            Alignment.Horizontal.CenterHorizontally)
        {
            Modifier.Weight(1f),
            number,
            caption,
        };
    }

    private static ComposableNode ChoiceAction(IComposer composer,
        AndroidVisualPalette palette, NativeQuizChoice choice, int index,
        NativeQuizState state, Action<string> answer, bool enabled)
    {
        var graded = state.Phase == NativePhase.Feedback;
        var correct = graded && string.Equals(choice.Text,
            state.CorrectAnswerAfterGrading, StringComparison.OrdinalIgnoreCase);
        var selectedWrong = graded && !correct && string.Equals(choice.Text,
            state.SelectedAnswer, StringComparison.OrdinalIgnoreCase);
        var selected = string.Equals(choice.Text, state.SelectedAnswer,
            StringComparison.OrdinalIgnoreCase);
        var color = selectedWrong ? palette.DangerSurface : palette.Card;
        var border = correct ? palette.Success
            : selectedWrong ? palette.Danger
            : selected ? palette.Primary : palette.Border;
        var caption = correct ? $" - {Localize(NativeText.QuizCorrect)}"
            : selectedWrong ? $" - {Localize(NativeText.QuizIncorrect)}" : string.Empty;
        var label = choice.Text + caption;
        var button = new ComposeButton(() => answer(choice.Text), enabled: enabled)
        {
            Modifier = Modifier.FillMaxWidth()
                .HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Border(graded && (correct || selectedWrong) ? 2 : 1,
                    border, AndroidVisualTheme.ControlRadius)
                .TestTag($"{NativeAccess.QuizChoice}.{index}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(label);
                    semantics.Selected(selected);
                }),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Colors = composer.ButtonColors(
                containerColor: color,
                contentColor: palette.Text,
                disabledContainerColor: color,
                disabledContentColor: palette.Text),
        };
        button.Add(new Text(label));
        return button;
    }
}
