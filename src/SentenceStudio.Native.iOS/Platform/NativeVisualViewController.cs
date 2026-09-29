#if !NATIVE_DASHBOARD_LABEL_TEST
using Foundation;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using SentenceStudio.Services.Progress;
using UIKit;
#endif

namespace SentenceStudio.Native.iOS.Platform;

#if !NATIVE_DASHBOARD_LABEL_TEST
internal sealed class NativeVisualViewController : UIViewController
{
    private readonly List<(SwiftView View, UIViewController Host)> _hosted = [];
    private readonly List<(UIView View, UIColor Color)> _borders = [];
    private UIStackView _header = null!;
    private UIStackView _body = null!;
    private UIStackView _footer = null!;
    private UILabel _title = null!;
    private UILabel _progress = null!;
    private UILabel _correct = null!;
    private UIProgressView _progressBar = null!;
    private Action<string>? _answerChanged;
    private Action<string>? _correctionChanged;
    private Action<string>? _openQuiz;
    private bool _insightExpanded;
    private bool _dashboardBusy;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = NativeVisualTheme.Background;
        View.TintColor = NativeVisualTheme.Primary;
        var statusChrome = new UIView
        {
            BackgroundColor = NativeVisualTheme.Header,
            TranslatesAutoresizingMaskIntoConstraints = false,
            IsAccessibilityElement = false,
        };
        View.AddSubview(statusChrome);
        NSLayoutConstraint.ActivateConstraints(
        [
            statusChrome.TopAnchor.ConstraintEqualTo(View.TopAnchor),
            statusChrome.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            statusChrome.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            statusChrome.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
        ]);

        _title = Label(string.Empty, UIFontTextStyle.Title2);
        _title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _title.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _header = new UIStackView([_title])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 10,
            LayoutMargins = new UIEdgeInsets(8, NativeVisualTheme.PagePadding,
                8, NativeVisualTheme.PagePadding),
            LayoutMarginsRelativeArrangement = true,
        };
        _header.BackgroundColor = NativeVisualTheme.Header;
        _header.HeightAnchor.ConstraintGreaterThanOrEqualTo(56).Active = true;

        _body = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 12,
            LayoutMargins = new UIEdgeInsets(16, NativeVisualTheme.PagePadding,
                24, NativeVisualTheme.PagePadding),
            LayoutMarginsRelativeArrangement = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var scroll = new UIScrollView
        {
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            BackgroundColor = NativeVisualTheme.Background,
        };
        scroll.AddSubview(_body);
        NSLayoutConstraint.ActivateConstraints(
        [
            _body.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor),
            _body.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor),
            _body.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor),
            _body.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor),
            _body.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor),
        ]);

        _progress = Label(string.Empty, UIFontTextStyle.Body, secondary: true);
        _progress.AccessibilityIdentifier = "native.quiz.progress";
        _correct = Label(string.Empty, UIFontTextStyle.Body);
        _correct.AccessibilityIdentifier = "native.quiz.correct-count";
        _progressBar = new UIProgressView(UIProgressViewStyle.Default)
        {
            ProgressTintColor = NativeVisualTheme.Primary,
            TrackTintColor = NativeVisualTheme.Border,
        };
        var progressRow = new UIStackView([_progress, _correct])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.EqualSpacing,
        };
        _footer = new UIStackView([progressRow, _progressBar])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            LayoutMargins = new UIEdgeInsets(12, NativeVisualTheme.PagePadding,
                12, NativeVisualTheme.PagePadding),
            LayoutMarginsRelativeArrangement = true,
            BackgroundColor = NativeVisualTheme.Header,
            Hidden = true,
            AccessibilityIdentifier = "native.quiz.footer",
        };

        var root = new UIStackView([_header, scroll, _footer])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        View.AddSubview(root);
        var bottom = root.BottomAnchor.ConstraintEqualTo(
            View.SafeAreaLayoutGuide.BottomAnchor);
        bottom.Priority = 750;
        NSLayoutConstraint.ActivateConstraints(
        [
            root.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            root.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor),
            root.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor),
            root.BottomAnchor.ConstraintLessThanOrEqualTo(
                View.KeyboardLayoutGuide.TopAnchor),
            bottom,
        ]);
    }

    public void Show(SwiftView page, string title,
        NativeDashboardState? dashboard = null, NativeQuizState? quiz = null,
        Action<string>? answerChanged = null,
        Action<string>? correctionChanged = null,
        Action<string>? openQuiz = null,
        Action? regenerate = null,
        Action? signOut = null,
        bool insightExpanded = false,
        bool preview = false,
        bool busy = false)
    {
        if (!IsViewLoaded)
            LoadViewIfNeeded();
        if (page.Kind != "scroll")
            throw new ArgumentException("The native journey requires a SwiftUI scroll body.", nameof(page));

        Clear();
        _answerChanged = answerChanged;
        _correctionChanged = correctionChanged;
        _openQuiz = openQuiz;
        _insightExpanded = insightExpanded;
        _dashboardBusy = dashboard is not null && busy;
        _title.Text = title;
        _body.AccessibilityIdentifier = quiz is null
            ? NativeAccess.Plan : "native.quiz.screen";
        while (page.Children.Count > 0)
        {
            var child = page.Children[0];
            page.RemoveAt(0);
            var id = child.AccessibilityIdentifier;
            if (id is "native.quiz.back" or "native.dashboard.refresh")
            {
                var headerControl = HostControl(child, primary: false);
                if (id == "native.quiz.back")
                    _header.InsertArrangedSubview(headerControl, 0);
                else
                    _header.AddArrangedSubview(headerControl);
            }
            else
                AddNode(_body, child, dashboard, quiz);
        }
        page.Dispose();
        if (dashboard is not null && signOut is not null)
        {
            var menuItems = new List<UIMenuElement>();
            if (dashboard.Phase == NativePhase.Ready && regenerate is not null)
                menuItems.Add(UIAction.Create(
                    LocalizationManager.Instance.GetString(NativeText.Regenerate),
                    UIImage.GetSystemImage("arrow.clockwise"), "native.dashboard.regenerate",
                    _ => regenerate()));
            menuItems.Add(UIAction.Create(
                preview ? "Exit Debug preview" : LocalizationManager.Instance.GetString("Nav_Logout"),
                UIImage.GetSystemImage("rectangle.portrait.and.arrow.right"),
                "native.dashboard.sign-out", _ => signOut()));
            var more = UIButton.FromType(UIButtonType.System);
            more.AccessibilityIdentifier = "native.dashboard.menu";
            more.AccessibilityLabel = LocalizationManager.Instance.GetString("More");
            more.SetImage(UIImage.GetSystemImage("ellipsis"), UIControlState.Normal);
            more.TintColor = NativeVisualTheme.Text;
            more.Enabled = !busy;
            more.Menu = UIMenu.Create(menuItems.ToArray());
            more.ShowsMenuAsPrimaryAction = true;
            more.WidthAnchor.ConstraintEqualTo(
                NativeVisualTheme.ControlHeight).Active = true;
            more.HeightAnchor.ConstraintGreaterThanOrEqualTo(
                NativeVisualTheme.ControlHeight).Active = true;
            _header.AddArrangedSubview(more);
        }

        _footer.Hidden = quiz?.Prompt is null
            || quiz.Phase is not (NativePhase.Ready or NativePhase.Feedback or NativePhase.Error)
            || quiz.Destination == NativeDestination.QuizSummary;
        if (!_footer.Hidden && quiz is not null)
        {
            _progress.Text = $"{quiz.CurrentTurn} / {quiz.TurnsInRound}";
            _correct.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationManager.Instance.GetString("VocabQuiz_CorrectCount"),
                quiz.CorrectCount);
            _progressBar.Progress = quiz.TurnsInRound == 0 ? 0
                : (float)quiz.CurrentTurn / quiz.TurnsInRound;
        }
    }

    private void AddNode(UIStackView parent, SwiftView node,
        NativeDashboardState? dashboard, NativeQuizState? quiz)
    {
        var id = node.AccessibilityIdentifier;
        if (node.Kind is "vstack" or "hstack")
        {
            var vocabRow = node.Kind == "hstack"
                && node.Children.Any(child => child.AccessibilityIdentifier?
                    .StartsWith("native.dashboard.vocab.stat.", StringComparison.Ordinal) == true);
            var content = new UIStackView
            {
                Axis = node.Kind == "hstack"
                    ? UILayoutConstraintAxis.Horizontal : UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = node.Kind == "hstack"
                    ? id is "native.plan.progress.row" or "native.dashboard.vocab.summary"
                        ? UIStackViewDistribution.EqualSpacing
                        : UIStackViewDistribution.FillEqually
                    : UIStackViewDistribution.Fill,
                Spacing = node.Kind == "hstack"
                    ? vocabRow ? 12 : 4 : 8,
            };
            while (node.Children.Count > 0)
            {
                var child = node.Children[0];
                node.RemoveAt(0);
                AddNode(content, child, dashboard, quiz);
            }
            node.Dispose();
            if (id?.StartsWith("native.plan.card.container.", StringComparison.Ordinal) == true
                && dashboard is not null)
            {
                var cardId = id["native.plan.card.container.".Length..];
                var card = dashboard.Cards.SingleOrDefault(item => item.Id == cardId);
                if (card is null)
                    throw new InvalidOperationException("A displayed plan card has no owner-scoped item.");
                parent.AddArrangedSubview(DashboardCard(content, card));
                return;
            }
            if (id == "native.plan.progress.row")
            {
                content.AccessibilityIdentifier = id;
                parent.AddArrangedSubview(content);
                return;
            }
            if (string.IsNullOrEmpty(id))
            {
                parent.AddArrangedSubview(content);
                return;
            }
            var shell = Card(content, id);
            if (id == "native.plan.progress" && dashboard is not null)
                AddProgress(content, dashboard.CompletionPercentage / 100, completed: true);
            parent.AddArrangedSubview(shell);
            return;
        }
        if (node.Kind == "text")
        {
            if (id == "native.dashboard.mode.today")
            {
                parent.AddArrangedSubview(SelectedPlanMode(node.Text, id));
                node.Dispose();
                return;
            }
            var label = Label(node.Text, TextStyle(id),
                secondary: id is "native.plan.card.metadata" or "native.plan.date"
                    or "native.plan.insight.resource.reason"
                    or "native.dashboard.vocab.accuracy"
                    || id.StartsWith("native.dashboard.vocab.stat.", StringComparison.Ordinal)
                        && id.EndsWith(".label", StringComparison.Ordinal)
                    || id.StartsWith(NativeAccess.QuizSummaryResult + ".", StringComparison.Ordinal)
                        && id.EndsWith(".meaning", StringComparison.Ordinal));
            label.AccessibilityIdentifier = id;
            if (id == NativeAccess.QuizQuestion)
            {
                label.Font = UIFontMetrics.GetMetrics(
                        UIFontTextStyle.LargeTitle.GetConstant()!.ToString())
                    .GetScaledFont(UIFont.BoldSystemFontOfSize(NativeVisualTheme.QuizPromptSize));
                label.TextColor = NativeVisualTheme.Primary;
                label.TextAlignment = UITextAlignment.Center;
            }
            if (id.StartsWith(NativeAccess.QuizSummaryResult + ".", StringComparison.Ordinal)
                && id.EndsWith(".word", StringComparison.Ordinal))
                label.TextColor = NativeVisualTheme.Primary;
            if (id.StartsWith("native.quiz.summary.stat.", StringComparison.Ordinal))
                label.TextAlignment = UITextAlignment.Center;
            if (id.StartsWith(NativeAccess.QuizChoice + ".", StringComparison.Ordinal)
                && quiz?.Phase == NativePhase.Ready
                && string.Equals(quiz.SelectedAnswer, node.Text,
                    StringComparison.OrdinalIgnoreCase))
                label.AccessibilityTraits = UIAccessibilityTrait.Selected;
            if (id is NativeAccess.QuizQuestion or "native.quiz.summary.title")
                label.AccessibilityTraits = UIAccessibilityTrait.Header;
            if (id == "native.plan.insight.resource.title")
                label.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
            if (id == "native.plan.insight.focus.title")
                label.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
            if (id == "native.dashboard.vocab.title")
            {
                label.Font = UIFontMetrics.GetMetrics(
                        UIFontTextStyle.Title2.GetConstant()!.ToString())
                    .GetScaledFont(UIFont.BoldSystemFontOfSize(22));
                label.AccessibilityTraits = UIAccessibilityTrait.Header;
            }
            if (id.StartsWith("native.dashboard.vocab.stat.", StringComparison.Ordinal))
            {
                label.TextAlignment = UITextAlignment.Center;
                if (id.EndsWith(".value", StringComparison.Ordinal))
                {
                    label.Font = UIFontMetrics.GetMetrics(
                            UIFontTextStyle.Title2.GetConstant()!.ToString())
                        .GetScaledFont(UIFont.BoldSystemFontOfSize(24));
                    label.TextColor = id.Contains(".learning.", StringComparison.Ordinal)
                        || id.Contains(".review.", StringComparison.Ordinal)
                        ? NativeVisualTheme.WarningText
                        : id.Contains(".known.", StringComparison.Ordinal)
                            ? NativeVisualTheme.KnownText : NativeVisualTheme.Primary;
                }
            }
            node.Dispose();
            if (id is NativeAccess.QuizFeedback or "native.quiz.error"
                or "native.dashboard.status" or "native.plan.insight.pattern")
            {
                var error = id == "native.quiz.error"
                    || id == "native.dashboard.status" && dashboard?.Phase
                        is NativePhase.Error or NativePhase.Blocked
                    || id == NativeAccess.QuizFeedback
                        && quiz?.AnswerWasCorrect == false;
                var alert = Card(label, id, alert: true, error: error);
                if (id == NativeAccess.QuizFeedback && quiz?.AnswerWasCorrect == true)
                    SetBorder(alert, NativeVisualTheme.Success);
                parent.AddArrangedSubview(alert);
            }
            else if (id?.StartsWith(NativeAccess.QuizChoice + ".", StringComparison.Ordinal)
                == true)
                parent.AddArrangedSubview(Choice(label, id, quiz));
            else
                parent.AddArrangedSubview(label);
            return;
        }
        if (node.Kind == "textField")
        {
            var placeholder = LocalizationManager.Instance.GetString(
                id == NativeAccess.QuizAnswer
                    ? NativeText.AnswerRequired : NativeText.CorrectionRequired);
            var field = new UITextField
            {
                Text = node.Text,
                Placeholder = placeholder,
                AccessibilityLabel = placeholder,
                AccessibilityIdentifier = id,
                Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
                AdjustsFontForContentSizeCategory = true,
                TextColor = NativeVisualTheme.Text,
                TintColor = NativeVisualTheme.Primary,
                AutocorrectionType = UITextAutocorrectionType.Default,
            };
            field.EditingChanged += (_, _) =>
            {
                if (id == NativeAccess.QuizAnswer)
                    _answerChanged?.Invoke(field.Text ?? string.Empty);
                else
                    _correctionChanged?.Invoke(field.Text ?? string.Empty);
            };
            node.Dispose();
            var wrapper = Card(field, null, compact: true);
            wrapper.HeightAnchor.ConstraintGreaterThanOrEqualTo(
                NativeVisualTheme.ControlHeight).Active = true;
            parent.AddArrangedSubview(wrapper);
            return;
        }
        if (id.StartsWith(NativeAccess.QuizChoice + ".", StringComparison.Ordinal))
            parent.AddArrangedSubview(Choice(
                HostControl(node, primary: false, decorate: false), id, quiz));
        else if (id == "native.plan.insight.toggle")
            parent.AddArrangedSubview(InsightHeader(node));
        else
            parent.AddArrangedSubview(HostControl(node,
                primary: id is "native.quiz.next" or "native.quiz.submit"
                    or "native.quiz.correction" or "native.quiz.continue"
                    or "native.quiz.resume.continue" or "native.dashboard.generate"));
    }

    private UIView Choice(UIView content, string id, NativeQuizState? quiz)
    {
        var indexText = id[(NativeAccess.QuizChoice.Length + 1)..];
        var index = int.TryParse(indexText, out var parsed) ? parsed : -1;
        var answer = index >= 0 && index < quiz?.Choices.Length
            ? quiz.Choices[index].Text : null;
        var correct = quiz?.Phase == NativePhase.Feedback
            && string.Equals(answer, quiz.CorrectAnswerAfterGrading,
                StringComparison.OrdinalIgnoreCase);
        var wrong = quiz?.Phase == NativePhase.Feedback && !correct
            && string.Equals(answer, quiz.SelectedAnswer,
                StringComparison.OrdinalIgnoreCase);
        var selected = quiz?.Phase == NativePhase.Ready
            && string.Equals(answer, quiz.SelectedAnswer,
                StringComparison.OrdinalIgnoreCase);
        var wrapper = Card(content, id + ".container", compact: true);
        SetBorder(wrapper, correct ? NativeVisualTheme.Success
            : wrong ? NativeVisualTheme.Danger
            : selected ? NativeVisualTheme.Primary : NativeVisualTheme.Border);
        if (wrong)
            wrapper.BackgroundColor = NativeVisualTheme.DangerSurface;
        return wrapper;
    }

    private UIView SelectedPlanMode(string title, string id)
    {
        var icon = new UIImageView(UIImage.GetSystemImage("calendar"))
        {
            TintColor = NativeVisualTheme.OnPrimary,
            ContentMode = UIViewContentMode.ScaleAspectFit,
            IsAccessibilityElement = false,
        };
        icon.WidthAnchor.ConstraintEqualTo(20).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(20).Active = true;
        var label = Label(title, UIFontTextStyle.Headline);
        label.TextColor = NativeVisualTheme.OnPrimary;
        var selected = new UIStackView([icon, label])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
            LayoutMargins = new UIEdgeInsets(10, 16, 10, 16),
            LayoutMarginsRelativeArrangement = true,
            BackgroundColor = NativeVisualTheme.Primary,
            AccessibilityIdentifier = id,
            AccessibilityLabel = title,
            AccessibilityTraits = UIAccessibilityTrait.Selected
                | UIAccessibilityTrait.StaticText,
            IsAccessibilityElement = true,
        };
        selected.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        selected.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        return selected;
    }

    private UIView InsightHeader(SwiftView node)
    {
        var button = (UIButton)HostControl(node, primary: false);
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Left;
        button.SetImage(UIImage.GetSystemImage("lightbulb"), UIControlState.Normal);
        button.SetTitleColor(NativeVisualTheme.Text, UIControlState.Normal);
        button.TintColor = NativeVisualTheme.Primary;
        button.AccessibilityValue = LocalizationManager.Instance.GetString(
            _insightExpanded ? "Dashboard_InsightExpanded" : "Dashboard_InsightCollapsed");
        button.SetContentHuggingPriority(250, UILayoutConstraintAxis.Horizontal);

        var chevron = new UIImageView(UIImage.GetSystemImage(
            _insightExpanded ? "chevron.up" : "chevron.down"))
        {
            TintColor = NativeVisualTheme.Text,
            ContentMode = UIViewContentMode.ScaleAspectFit,
            IsAccessibilityElement = false,
            AccessibilityIdentifier = "native.plan.insight.chevron",
        };
        chevron.WidthAnchor.ConstraintEqualTo(20).Active = true;
        chevron.HeightAnchor.ConstraintEqualTo(20).Active = true;
        var row = new UIStackView([button, chevron])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
        };
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        return row;
    }

    private UIView DashboardCard(UIStackView content, NativePlanCard card)
    {
        var title = content.ArrangedSubviews.OfType<UILabel>()
            .FirstOrDefault(view => view.AccessibilityIdentifier == "native.plan.card.title")
            ?.Text ?? card.Title;
        var metadata = content.ArrangedSubviews.OfType<UILabel>()
            .FirstOrDefault(view => view.AccessibilityIdentifier == "native.plan.card.metadata")
            ?.Text ?? string.Empty;
        content.Alpha = card.IsCompleted ? 0.75f : 1f;
        if (!card.IsCompleted && card.MinutesSpent > 0 && card.EstimatedMinutes > 0)
            AddProgress(content, (double)card.MinutesSpent / card.EstimatedMinutes,
                completed: false);

        var iconName = card.IsCompleted ? "checkmark.circle.fill" : card.Activity switch
        {
            PlanActivityType.VocabularyReview => "checklist",
            PlanActivityType.Listening or PlanActivityType.Shadowing => "headphones",
            PlanActivityType.Writing or PlanActivityType.Translation => "square.and.pencil",
            PlanActivityType.Reading => "book",
            PlanActivityType.VideoWatching => "play.rectangle",
            PlanActivityType.SceneDescription => "photo",
            PlanActivityType.Conversation => "text.bubble",
            PlanActivityType.NumberDrill => "number",
            _ => "square.grid.2x2",
        };
        var icon = new UIImageView(UIImage.GetSystemImage(iconName))
        {
            TintColor = card.IsCompleted
                ? NativeVisualTheme.Success : NativeVisualTheme.Primary,
            ContentMode = UIViewContentMode.ScaleAspectFit,
            IsAccessibilityElement = false,
        };
        icon.WidthAnchor.ConstraintEqualTo(24).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(24).Active = true;
        content.SetContentHuggingPriority(250, UILayoutConstraintAxis.Horizontal);
        var row = new UIStackView([icon, content])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
        };
        var canOpen = card.Activity == PlanActivityType.VocabularyReview
            && !card.IsCompleted && _openQuiz is not null;
        if (canOpen)
        {
            var chevron = new UIImageView(UIImage.GetSystemImage("chevron.right"))
            {
                TintColor = NativeVisualTheme.SecondaryText,
                ContentMode = UIViewContentMode.ScaleAspectFit,
                IsAccessibilityElement = false,
            };
            chevron.WidthAnchor.ConstraintEqualTo(14).Active = true;
            chevron.HeightAnchor.ConstraintEqualTo(20).Active = true;
            row.AddArrangedSubview(chevron);
        }
        var shell = Card(row, $"native.plan.card.container.{card.Id}");
        var completedStatus = card.IsCompleted
            ? content.ArrangedSubviews.OfType<UILabel>()
                .FirstOrDefault(view => view.AccessibilityIdentifier
                    == "native.plan.card.completed")?.Text
                ?? LocalizationManager.Instance.GetString("PlanSummary_Completed")
            : null;
        var accessibilityLabel = NativeDashboardCardAccessibility.Label(
            title, metadata, completedStatus);
        if (canOpen)
        {
            var button = UIButton.FromType(UIButtonType.Custom);
            button.AccessibilityIdentifier = $"{NativeAccess.PlanCard}.{card.Id}";
            button.AccessibilityLabel = accessibilityLabel;
            button.AccessibilityTraits = UIAccessibilityTrait.Button;
            button.TranslatesAutoresizingMaskIntoConstraints = false;
            button.TouchUpInside += (_, _) => _openQuiz?.Invoke(card.Id);
            shell.AddSubview(button);
            NSLayoutConstraint.ActivateConstraints(
            [
                button.TopAnchor.ConstraintEqualTo(shell.TopAnchor),
                button.BottomAnchor.ConstraintEqualTo(shell.BottomAnchor),
                button.LeadingAnchor.ConstraintEqualTo(shell.LeadingAnchor),
                button.TrailingAnchor.ConstraintEqualTo(shell.TrailingAnchor),
            ]);
        }
        else
        {
            shell.AccessibilityIdentifier = $"{NativeAccess.PlanCard}.{card.Id}";
            shell.AccessibilityLabel = accessibilityLabel;
            shell.AccessibilityTraits = UIAccessibilityTrait.StaticText;
            shell.IsAccessibilityElement = true;
        }
        return shell;
    }

    private UIView HostControl(SwiftView node, bool primary, bool decorate = true)
    {
        var id = node.AccessibilityIdentifier;
        var host = SwiftUIHost.CreateController(node);
        if (node.Kind == "button")
        {
            var button = UIButton.FromType(UIButtonType.System);
            button.AccessibilityIdentifier = id;
            button.SetTitle(node.Text, UIControlState.Normal);
            button.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(
                UIFontTextStyle.Body);
            button.TitleLabel.AdjustsFontForContentSizeCategory = true;
            button.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
            button.HeightAnchor.ConstraintGreaterThanOrEqualTo(
                NativeVisualTheme.ControlHeight).Active = true;
            if (id is "native.quiz.back" or "native.dashboard.refresh")
                button.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
            var inline = !decorate
                || id is "native.quiz.back" or "native.dashboard.refresh"
                    or "native.plan.insight.toggle"
                || id.StartsWith(NativeAccess.PlanCard + ".", StringComparison.Ordinal);
            button.BackgroundColor = inline ? UIColor.Clear
                : primary ? NativeVisualTheme.Primary : NativeVisualTheme.Elevated;
            button.SetTitleColor(primary && !inline
                    ? NativeVisualTheme.OnPrimary : NativeVisualTheme.Primary,
                UIControlState.Normal);
            if (id == "native.dashboard.refresh")
            {
                button.Enabled = !_dashboardBusy;
                button.AccessibilityLabel = node.Text;
                button.SetTitle(null, UIControlState.Normal);
                button.SetImage(UIImage.GetSystemImage("arrow.clockwise"),
                    UIControlState.Normal);
                button.TintColor = NativeVisualTheme.Text;
                button.WidthAnchor.ConstraintEqualTo(
                    NativeVisualTheme.ControlHeight).Active = true;
            }
            button.TouchUpInside += (_, _) => node.InvokeAction();
            _hosted.Add((node, host));
            return button;
        }
        AddChildViewController(host);
        var hostedView = host.View!;
        hostedView.BackgroundColor = UIColor.Clear;
        hostedView.TintColor = primary ? NativeVisualTheme.OnPrimary
            : NativeVisualTheme.Primary;
        hostedView.TranslatesAutoresizingMaskIntoConstraints = false;
        if (!decorate)
        {
            hostedView.HeightAnchor.ConstraintGreaterThanOrEqualTo(
                NativeVisualTheme.ControlHeight).Active = true;
            host.DidMoveToParentViewController(this);
            _hosted.Add((node, host));
            return hostedView;
        }
        var container = Card(hostedView, id, compact: true);
        container.BackgroundColor = primary ? NativeVisualTheme.Primary
            : NativeVisualTheme.Elevated;
        container.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        host.DidMoveToParentViewController(this);
        _hosted.Add((node, host));
        return container;
    }

    private UIView Card(UIView content, string? id,
        bool compact = false, bool alert = false, bool error = false)
    {
        var shell = new UIView
        {
            BackgroundColor = error ? NativeVisualTheme.DangerSurface
                : compact ? NativeVisualTheme.Elevated : NativeVisualTheme.Card,
            AccessibilityIdentifier = id,
        };
        shell.Layer.CornerRadius = compact
            ? NativeVisualTheme.ControlRadius : NativeVisualTheme.CardRadius;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        shell.AddSubview(content);
        var inset = compact ? 8 : NativeVisualTheme.CardPadding;
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(shell.TopAnchor, inset),
            content.BottomAnchor.ConstraintEqualTo(shell.BottomAnchor, -inset),
            content.LeadingAnchor.ConstraintEqualTo(shell.LeadingAnchor, inset),
            content.TrailingAnchor.ConstraintEqualTo(shell.TrailingAnchor, -inset),
        ]);
        if (compact || alert)
            SetBorder(shell, error ? NativeVisualTheme.Danger : NativeVisualTheme.Border);
        return shell;
    }

    private void SetBorder(UIView view, UIColor color)
    {
        view.Layer.BorderWidth = 1;
        view.Layer.BorderColor = color.CGColor;
        _borders.Add((view, color));
    }

    private static void AddProgress(UIStackView parent, double amount, bool completed)
    {
        var progress = new UIProgressView(UIProgressViewStyle.Default)
        {
            Progress = (float)Math.Clamp(amount, 0, 1),
            ProgressTintColor = completed ? NativeVisualTheme.Success
                : NativeVisualTheme.Primary,
            TrackTintColor = completed
                ? NativeVisualTheme.Elevated : NativeVisualTheme.Border,
            ClipsToBounds = true,
        };
        progress.Layer.CornerRadius = 3;
        progress.HeightAnchor.ConstraintGreaterThanOrEqualTo(6).Active = true;
        parent.AddArrangedSubview(progress);
    }

    private static UIFontTextStyle TextStyle(string? id) => id switch
    {
        NativeAccess.QuizQuestion => UIFontTextStyle.LargeTitle,
        "native.quiz.correct-answer" or "native.quiz.summary.title"
            => UIFontTextStyle.Title2,
        "native.plan.progress.title" => UIFontTextStyle.Headline,
        "native.plan.progress.count" => UIFontTextStyle.Footnote,
        "native.dashboard.vocab.title" => UIFontTextStyle.Title2,
        "native.dashboard.vocab.total" or "native.dashboard.vocab.accuracy"
            => UIFontTextStyle.Subheadline,
        var value when value?.EndsWith(".value", StringComparison.Ordinal) == true
            => UIFontTextStyle.Title2,
        var value when value?.EndsWith(".label", StringComparison.Ordinal) == true
            => UIFontTextStyle.Caption1,
        "native.plan.card.title" => UIFontTextStyle.Headline,
        "native.plan.card.metadata" or "native.plan.date" => UIFontTextStyle.Subheadline,
        var value when value?.StartsWith(
            NativeAccess.QuizSummaryResult + ".", StringComparison.Ordinal) == true
            && value.EndsWith(".word", StringComparison.Ordinal)
            => UIFontTextStyle.Headline,
        _ => UIFontTextStyle.Body,
    };

    private static UILabel Label(string text, UIFontTextStyle style, bool secondary = false) =>
        new()
        {
            Text = text,
            Font = UIFont.GetPreferredFontForTextStyle(style),
            TextColor = secondary ? NativeVisualTheme.SecondaryText : NativeVisualTheme.Text,
            AdjustsFontForContentSizeCategory = true,
            Lines = 0,
        };

    private void Clear()
    {
        foreach (var (node, host) in _hosted)
        {
            if (host.ParentViewController is not null)
            {
                host.WillMoveToParentViewController(null);
                host.ViewIfLoaded?.RemoveFromSuperview();
                host.RemoveFromParentViewController();
            }
            SwiftUIHost.DetachController(node, host);
            node.Dispose();
        }
        _hosted.Clear();
        _borders.Clear();
        foreach (var view in _body.ArrangedSubviews.ToArray())
        {
            _body.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        foreach (var view in _header.ArrangedSubviews
            .Where(view => !ReferenceEquals(view, _title)).ToArray())
        {
            _header.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
    }

    public override void TraitCollectionDidChange(UITraitCollection? previousTraitCollection)
    {
        base.TraitCollectionDidChange(previousTraitCollection);
        foreach (var (view, color) in _borders)
            view.Layer.BorderColor = color.CGColor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && IsViewLoaded)
            Clear();
        base.Dispose(disposing);
    }
}
#endif

internal static class NativeDashboardCardAccessibility
{
    internal static string Label(string title, string metadata, string? completedStatus) =>
        string.Join(". ", new[] { title, metadata, completedStatus }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}
