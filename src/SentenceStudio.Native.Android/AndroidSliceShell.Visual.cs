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
        _screenHeaderBar ??= bar; // the shell adds the Sam entry to it (WithSam)
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

    /// <summary>
    /// Compose popups (DropdownMenu, ModalBottomSheet) render in their own window
    /// with a separate semantics root that does not inherit the shell root's
    /// testTagsAsResourceId; republish it there so their test tags stay addressable.
    /// </summary>
    private static Modifier PopupWindowSemantics(Modifier modifier) =>
        modifier.Semantics(semantics => semantics.TestTagsAsResourceId(true));

    private static DropdownMenu PopupMenu(bool expanded, Action onDismissRequest) =>
        new(expanded, onDismissRequest)
        {
            Modifier = Modifier.Semantics(semantics => semantics.TestTagsAsResourceId(true)),
        };

    /// <summary>
    /// Study's header (iOS DashboardView.WithToolbar): History (the practice log calendar),
    /// Regenerate while a plan is shown, and Refresh. Sign out moved to the Settings tab and
    /// the other destinations to the Library.
    /// </summary>
    private ComposableNode DashboardAppBar(AndroidVisualPalette palette)
    {
        var actions = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically);
        actions.Add(HeaderIconButton(Localize("Nav_History"), "native.study.history",
            OpenActivityLog, new Icon(Icons.Default.DateRange, null) { Modifier = Modifier.Size(24) }, palette));
        if (!Dashboard.IsNewUser || _preview)
        {
            if (Dashboard.Phase == NativePhase.Ready)
                actions.Add(HeaderIconButton(Localize(NativeText.Regenerate), "native.dashboard.regenerate",
                    RegeneratePlan, new Icon(Resource.Drawable.ic_auto_awesome, null) { Modifier = Modifier.Size(24) }, palette));
            actions.Add(HeaderIconButton(Localize("Common_Refresh"), "native.dashboard.refresh",
                RefreshPlan, new Icon(Icons.Default.Refresh, null) { Modifier = Modifier.Size(24) }, palette));
        }
        return new TopAppBar
        {
            Modifier = Modifier.FillMaxWidth().TestTag("native.dashboard.appbar"),
            Title = Heading(Localize(Dashboard.IsNewUser && !_preview
                    ? "Dashboard_Welcome" : "Dashboard_Title"), palette, 20,
                Modifier.Semantics(semantics => semantics.Heading())),
            Actions = actions,
        };
    }

    private ComposableNode HeaderIconButton(string label, string tag, Action command, Icon icon,
        AndroidVisualPalette palette)
    {
        var button = new IconButton(command, enabled: !Busy.Value)
        {
            Modifier = Modifier.TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
        };
        icon.Tint = palette.Text;
        button.Add(icon);
        return button;
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

    /// <summary>
    /// One multiple-choice option. With <paramref name="letter"/> (target-language
    /// answer audio) the option shows only its letter, so the written answer is
    /// not on screen until it is graded; its accessible name is "Choose option A".
    /// </summary>
    private static ComposableNode ChoiceAction(IComposer composer,
        AndroidVisualPalette palette, NativeQuizChoice choice, int index,
        NativeQuizState state, Action<string> answer, bool enabled,
        string? letter = null)
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
        var label = (letter ?? choice.Text) + caption;
        var accessibleName = (letter is null ? choice.Text
            : string.Format(CultureInfo.CurrentCulture,
                Localize("NativeVocabQuiz_ChooseChoice"), letter)) + caption;
        var button = new ComposeButton(() => answer(choice.Text), enabled: enabled)
        {
            Modifier = Modifier.FillMaxWidth()
                .HeightIn(min: AndroidVisualTheme.ControlHeight)
                .Border(graded && (correct || selectedWrong) ? 2 : 1,
                    border, AndroidVisualTheme.ControlRadius)
                .TestTag($"{NativeAccess.QuizChoice}.{index}")
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(accessibleName);
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
