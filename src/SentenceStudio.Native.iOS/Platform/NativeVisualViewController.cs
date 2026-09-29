using Foundation;
using Microsoft.SwiftUI;
using SentenceStudio.Native;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

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

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = NativeVisualTheme.Background;
        View.TintColor = NativeVisualTheme.Primary;

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
        _header.BackgroundColor = NativeVisualTheme.Background;
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
            BackgroundColor = NativeVisualTheme.Background,
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
        Action<string>? correctionChanged = null)
    {
        if (!IsViewLoaded)
            LoadViewIfNeeded();
        if (page.Kind != "scroll")
            throw new ArgumentException("The native journey requires a SwiftUI scroll body.", nameof(page));

        Clear();
        _answerChanged = answerChanged;
        _correctionChanged = correctionChanged;
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
            var content = new UIStackView
            {
                Axis = node.Kind == "hstack"
                    ? UILayoutConstraintAxis.Horizontal : UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = node.Kind == "hstack"
                    ? UIStackViewDistribution.FillEqually : UIStackViewDistribution.Fill,
                Spacing = node.Kind == "hstack" ? 4 : 8,
            };
            while (node.Children.Count > 0)
            {
                var child = node.Children[0];
                node.RemoveAt(0);
                AddNode(content, child, dashboard, quiz);
            }
            node.Dispose();
            if (string.IsNullOrEmpty(id))
            {
                parent.AddArrangedSubview(content);
                return;
            }
            var shell = Card(content, id);
            if (id == "native.plan.progress" && dashboard is not null)
                AddProgress(content, dashboard.CompletionPercentage / 100,
                    dashboard.CompletedCount == dashboard.TotalCount);
            if (id?.StartsWith("native.plan.card.", StringComparison.Ordinal) == true
                && content.ArrangedSubviews.Any(item =>
                    item.AccessibilityIdentifier == "native.plan.card.completed"))
            {
                SetBorder(shell, NativeVisualTheme.Success);
                AddProgress(content, 1, completed: true);
            }
            parent.AddArrangedSubview(shell);
            return;
        }
        if (node.Kind == "text")
        {
            var label = Label(node.Text, TextStyle(id),
                secondary: id is "native.plan.card.metadata" or "native.plan.date"
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
                || id.StartsWith(NativeAccess.PlanCard + ".", StringComparison.Ordinal);
            button.BackgroundColor = inline ? UIColor.Clear
                : primary ? NativeVisualTheme.Primary : NativeVisualTheme.Elevated;
            button.SetTitleColor(primary && !inline
                    ? NativeVisualTheme.OnPrimary : NativeVisualTheme.Primary,
                UIControlState.Normal);
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
        parent.AddArrangedSubview(new UIProgressView(UIProgressViewStyle.Default)
        {
            Progress = (float)Math.Clamp(amount, 0, 1),
            ProgressTintColor = completed ? NativeVisualTheme.Success
                : NativeVisualTheme.Primary,
            TrackTintColor = NativeVisualTheme.Border,
        });
    }

    private static UIFontTextStyle TextStyle(string? id) => id switch
    {
        NativeAccess.QuizQuestion => UIFontTextStyle.LargeTitle,
        "native.quiz.correct-answer" or "native.quiz.summary.title"
            or "native.plan.progress.title" => UIFontTextStyle.Title2,
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
