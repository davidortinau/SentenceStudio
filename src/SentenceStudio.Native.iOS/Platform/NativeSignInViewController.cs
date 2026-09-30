using Foundation;
using SentenceStudio.Abstractions;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeSignInViewController(
    Action<string, string, bool> signIn,
    Action retry) : UIViewController
{
    private UITextField _email = null!;
    private UITextField _password = null!;
    private UIButton _passwordToggle = null!;
    private UISwitch _remember = null!;
    private UILabel _status = null!;
    private UIView _statusSurface = null!;
    private UIButton _submit = null!;
    private UIButton _retry = null!;
    private UIActivityIndicatorView _spinner = null!;
    private UIScrollView _scroll = null!;
    private bool _canSubmit;
    private bool _showPassword;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = NativeVisualTheme.Background;
        View.TintColor = NativeVisualTheme.Primary;
        View.KeyboardLayoutGuide.UsesBottomSafeArea = false;

        var title = new UILabel
        {
            Text = "SentenceStudio",
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.LargeTitle),
            TextColor = NativeVisualTheme.Primary,
            TextAlignment = UITextAlignment.Center,
            AdjustsFontForContentSizeCategory = true,
            AccessibilityTraits = UIAccessibilityTrait.Header
        };
        var heading = new UILabel
        {
            Text = Text(NativeText.SignIn),
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Title2),
            TextColor = NativeVisualTheme.Text,
            TextAlignment = UITextAlignment.Center,
            AdjustsFontForContentSizeCategory = true,
            AccessibilityTraits = UIAccessibilityTrait.Header
        };
        _email = new UITextField
        {
            Placeholder = Text(NativeText.Email),
            AccessibilityLabel = Text(NativeText.Email),
            AccessibilityIdentifier = NativeAccess.Email,
            BorderStyle = UITextBorderStyle.RoundedRect,
            BackgroundColor = NativeVisualTheme.Elevated,
            TextColor = NativeVisualTheme.Text,
            TextContentType = UITextContentType.EmailAddress,
            KeyboardType = UIKeyboardType.EmailAddress,
            AutocorrectionType = UITextAutocorrectionType.No,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            ReturnKeyType = UIReturnKeyType.Next,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true
        };
        _email.EditingDidEndOnExit += (_, _) => _password.BecomeFirstResponder();
        _password = new UITextField
        {
            Placeholder = Text(NativeText.Password),
            AccessibilityLabel = Text(NativeText.Password),
            AccessibilityIdentifier = NativeAccess.Password,
            BorderStyle = UITextBorderStyle.RoundedRect,
            BackgroundColor = NativeVisualTheme.Elevated,
            TextColor = NativeVisualTheme.Text,
            SecureTextEntry = true,
            TextContentType = UITextContentType.Password,
            ReturnKeyType = UIReturnKeyType.Go,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true
        };
        _password.EditingDidEndOnExit += (_, _) => SignIn();
        _passwordToggle = UIButton.FromType(UIButtonType.System);
        _passwordToggle.AccessibilityIdentifier = NativeAccess.PasswordVisibility;
        _passwordToggle.AccessibilityLabel = Text(NativeText.ShowPassword);
        _passwordToggle.SetImage(UIImage.GetSystemImage("eye"), UIControlState.Normal);
        _passwordToggle.TintColor = NativeVisualTheme.Primary;
        _passwordToggle.TouchUpInside += (_, _) => TogglePasswordVisibility();
        var passwordRow = new UIStackView([_password, _passwordToggle])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
        };
        _remember = new UISwitch
        {
            On = false,
            OnTintColor = NativeVisualTheme.Primary,
            AccessibilityLabel = Text(NativeText.RememberMe),
            AccessibilityIdentifier = NativeAccess.RememberMe,
        };
        var rememberLabel = new UILabel
        {
            Text = Text(NativeText.RememberMe),
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true,
            TextColor = NativeVisualTheme.Text,
        };
        var rememberRow = new UIStackView([_remember, rememberLabel])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
        };
        _email.EditingChanged += (_, _) => UpdateSubmitAvailability();
        _password.EditingChanged += (_, _) => UpdateSubmitAvailability();
        _status = new UILabel
        {
            AccessibilityIdentifier = NativeAccess.AuthError,
            TextColor = NativeVisualTheme.Text,
            Lines = 0,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _statusSurface = new UIView
        {
            BackgroundColor = NativeVisualTheme.DangerSurface,
            Hidden = true,
        };
        _statusSurface.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        _statusSurface.AddSubview(_status);
        NSLayoutConstraint.ActivateConstraints(
        [
            _status.LeadingAnchor.ConstraintEqualTo(_statusSurface.LeadingAnchor, 12),
            _status.TrailingAnchor.ConstraintEqualTo(_statusSurface.TrailingAnchor, -12),
            _status.TopAnchor.ConstraintEqualTo(_statusSurface.TopAnchor, 12),
            _status.BottomAnchor.ConstraintEqualTo(_statusSurface.BottomAnchor, -12),
        ]);
        _submit = UIButton.FromType(UIButtonType.System);
        _submit.SetTitle(Text(NativeText.SignIn), UIControlState.Normal);
        _submit.AccessibilityIdentifier = NativeAccess.SignIn;
        _submit.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
        _submit.BackgroundColor = NativeVisualTheme.Primary;
        _submit.SetTitleColor(NativeVisualTheme.OnPrimary, UIControlState.Normal);
        _submit.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        _submit.TouchUpInside += (_, _) => SignIn();

        _retry = UIButton.FromType(UIButtonType.System);
        _retry.SetTitle(Text(NativeText.Retry), UIControlState.Normal);
        _retry.AccessibilityIdentifier = "native.auth.retry";
        _retry.BackgroundColor = NativeVisualTheme.Elevated;
        _retry.SetTitleColor(NativeVisualTheme.Text, UIControlState.Normal);
        _retry.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        _retry.TouchUpInside += (_, _) => retry();
        _retry.Hidden = true;
        _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
        {
            HidesWhenStopped = true,
            Color = NativeVisualTheme.Primary,
        };

        var form = new UIStackView([heading, _statusSurface,
            _email, passwordRow, rememberRow, _submit, _retry, _spinner])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 16,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var card = new UIView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            BackgroundColor = NativeVisualTheme.Card,
        };
        card.Layer.CornerRadius = NativeVisualTheme.CardRadius;
        card.AddSubview(form);
        NSLayoutConstraint.ActivateConstraints(
        [
            form.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor,
                NativeVisualTheme.CardPadding),
            form.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor,
                -NativeVisualTheme.CardPadding),
            form.TopAnchor.ConstraintEqualTo(card.TopAnchor,
                NativeVisualTheme.CardPadding),
            form.BottomAnchor.ConstraintEqualTo(card.BottomAnchor,
                -NativeVisualTheme.CardPadding),
        ]);
        var content = new UIStackView([title, card])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 20,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _scroll = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
            AutomaticallyAdjustsScrollIndicatorInsets = false,
        };
        View.AddSubview(_scroll);
        _scroll.AddSubview(content);
        var constraints = new List<NSLayoutConstraint>
        {
            _scroll.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(View.KeyboardLayoutGuide.TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor),
            content.TopAnchor.ConstraintGreaterThanOrEqualTo(
                _scroll.ContentLayoutGuide.TopAnchor, 20),
            content.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor, -20),
            _email.HeightAnchor.ConstraintGreaterThanOrEqualTo(NativeVisualTheme.ControlHeight),
            _password.HeightAnchor.ConstraintGreaterThanOrEqualTo(NativeVisualTheme.ControlHeight),
            _passwordToggle.WidthAnchor.ConstraintEqualTo(NativeVisualTheme.ControlHeight),
            _passwordToggle.HeightAnchor.ConstraintGreaterThanOrEqualTo(
                NativeVisualTheme.ControlHeight),
            _submit.HeightAnchor.ConstraintGreaterThanOrEqualTo(NativeVisualTheme.ControlHeight),
            _retry.HeightAnchor.ConstraintGreaterThanOrEqualTo(NativeVisualTheme.ControlHeight),
        };
        var centered = content.CenterYAnchor.ConstraintEqualTo(
            _scroll.FrameLayoutGuide.CenterYAnchor);
        centered.Priority = 750;
        constraints.Add(centered);
        if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad)
        {
            constraints.Add(content.WidthAnchor.ConstraintEqualTo(520));
            constraints.Add(_scroll.ContentLayoutGuide.WidthAnchor.ConstraintEqualTo(
                _scroll.FrameLayoutGuide.WidthAnchor));
            constraints.Add(content.CenterXAnchor.ConstraintEqualTo(
                _scroll.FrameLayoutGuide.CenterXAnchor));
        }
        else
        {
            // NSLayoutDimension's two-argument overload multiplies; use the three-argument offset overload.
            constraints.Add(content.LeadingAnchor.ConstraintEqualTo(
                _scroll.ContentLayoutGuide.LeadingAnchor,
                NativeVisualTheme.PagePadding));
            constraints.Add(content.TrailingAnchor.ConstraintEqualTo(
                _scroll.ContentLayoutGuide.TrailingAnchor,
                -NativeVisualTheme.PagePadding));
            constraints.Add(content.WidthAnchor.ConstraintEqualTo(
                _scroll.FrameLayoutGuide.WidthAnchor, 1,
                -2 * NativeVisualTheme.PagePadding));
        }
        NSLayoutConstraint.ActivateConstraints(constraints.ToArray());
        UpdateBottomInsets();
    }

    public override void ViewSafeAreaInsetsDidChange()
    {
        base.ViewSafeAreaInsetsDidChange();
        UpdateBottomInsets();
    }

    private void UpdateBottomInsets()
    {
        if (_scroll is null)
            return;
        var bottom = View!.SafeAreaInsets.Bottom;
        _scroll.ContentInset = new UIEdgeInsets(0, 0, bottom, 0);
        _scroll.VerticalScrollIndicatorInsets = new UIEdgeInsets(0, 0, bottom, 0);
    }

    public void ShowState(NativeAuthState state, bool busy, bool canAuthenticate)
    {
        if (!IsViewLoaded)
            LoadViewIfNeeded();

        var loading = busy || state.Phase == NativePhase.Loading;
        _spinner.Hidden = !loading;
        if (loading)
            _spinner.StartAnimating();
        else
            _spinner.StopAnimating();
        _canSubmit = !loading && canAuthenticate;
        UpdateSubmitAvailability();
        _email.Enabled = !loading && canAuthenticate;
        _password.Enabled = !loading && canAuthenticate;
        _passwordToggle.Enabled = !loading && canAuthenticate;
        _remember.Enabled = !loading && canAuthenticate;
        _retry.Hidden = loading || state.Phase is not (NativePhase.PendingSync or NativePhase.Error);
        _statusSurface.Hidden = state.ErrorKey is null && !loading;
        _status.Text = loading ? Text("Common_Loading")
            : state.ErrorKey is { } error ? Text(error) : null;
    }

    private void UpdateSubmitAvailability()
    {
        _submit.Enabled = _canSubmit
            && !string.IsNullOrWhiteSpace(_email.Text)
            && !string.IsNullOrWhiteSpace(_password.Text);
        _submit.Alpha = _submit.Enabled ? 1 : 0.55f;
    }

    private void SignIn()
    {
        if (!_submit.Enabled)
            return;
        var password = _password.Text ?? string.Empty;
        _password.Text = string.Empty;
        _password.ResignFirstResponder();
        signIn(_email.Text ?? string.Empty, password, _remember.On);
    }

    private void TogglePasswordVisibility()
    {
        var value = _password.Text;
        _showPassword = !_showPassword;
        _password.SecureTextEntry = !_showPassword;
        _password.Text = value;
        _passwordToggle.SetImage(UIImage.GetSystemImage(
            _showPassword ? "eye.slash" : "eye"), UIControlState.Normal);
        _passwordToggle.AccessibilityLabel = Text(_showPassword
            ? NativeText.HidePassword : NativeText.ShowPassword);
    }

    private static string Text(string key)
    {
        var value = LocalizationManager.Instance.GetString(
            key, System.Globalization.CultureInfo.CurrentUICulture);
        return value == key
            ? throw new InvalidOperationException($"Missing sign-in text resource: {key}")
            : value;
    }
}
