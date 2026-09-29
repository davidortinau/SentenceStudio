using Foundation;
using SentenceStudio.Abstractions;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

public sealed class NativeSignInViewController(
    Action<string, string> signIn,
    Action retry) : UIViewController
{
    private UITextField _email = null!;
    private UITextField _password = null!;
    private UILabel _status = null!;
    private UIButton _submit = null!;
    private UIButton _retry = null!;
    private UIActivityIndicatorView _spinner = null!;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.SystemBackground;

        var title = new UILabel
        {
            Text = "SentenceStudio",
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.LargeTitle),
            AdjustsFontForContentSizeCategory = true,
            AccessibilityTraits = UIAccessibilityTrait.Header
        };
        var heading = new UILabel
        {
            Text = Text(NativeText.SignIn),
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Title2),
            AdjustsFontForContentSizeCategory = true,
            AccessibilityTraits = UIAccessibilityTrait.Header
        };
        _email = new UITextField
        {
            Placeholder = Text(NativeText.Email),
            AccessibilityLabel = Text(NativeText.Email),
            AccessibilityIdentifier = NativeAccess.Email,
            BorderStyle = UITextBorderStyle.RoundedRect,
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
            SecureTextEntry = true,
            TextContentType = UITextContentType.Password,
            ReturnKeyType = UIReturnKeyType.Go,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true
        };
        _password.EditingDidEndOnExit += (_, _) => SignIn();
        _status = new UILabel
        {
            AccessibilityIdentifier = NativeAccess.AuthError,
            TextColor = UIColor.Label,
            Lines = 0,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true,
            Hidden = true
        };
        _submit = UIButton.FromType(UIButtonType.System);
        _submit.SetTitle(Text(NativeText.SignIn), UIControlState.Normal);
        _submit.AccessibilityIdentifier = NativeAccess.SignIn;
        _submit.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
        _submit.TouchUpInside += (_, _) => SignIn();

        _retry = UIButton.FromType(UIButtonType.System);
        _retry.SetTitle(Text(NativeText.Retry), UIControlState.Normal);
        _retry.AccessibilityIdentifier = "native.auth.retry";
        _retry.TouchUpInside += (_, _) => retry();
        _retry.Hidden = true;
        _spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
        {
            HidesWhenStopped = true
        };

        var form = new UIStackView([title, heading, _email, _password, _status,
            _submit, _retry, _spinner])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 16,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        form.SetCustomSpacing(32, title);
        View.AddSubview(form);
        var constraints = new List<NSLayoutConstraint>
        {
            form.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 28),
            _email.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            _password.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            _submit.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            _retry.HeightAnchor.ConstraintGreaterThanOrEqualTo(44)
        };
        if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad)
        {
            constraints.Add(form.CenterXAnchor.ConstraintEqualTo(
                View.SafeAreaLayoutGuide.CenterXAnchor));
            constraints.Add(form.WidthAnchor.ConstraintEqualTo(520));
        }
        else
        {
            constraints.Add(form.LeadingAnchor.ConstraintEqualTo(
                View.SafeAreaLayoutGuide.LeadingAnchor, 20));
            constraints.Add(form.TrailingAnchor.ConstraintEqualTo(
                View.SafeAreaLayoutGuide.TrailingAnchor, -20));
        }
        NSLayoutConstraint.ActivateConstraints(constraints.ToArray());
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
        _submit.Enabled = !loading && canAuthenticate;
        _email.Enabled = !loading;
        _password.Enabled = !loading;
        _retry.Hidden = loading || state.Phase is not (NativePhase.PendingSync or NativePhase.Error);
        _status.Hidden = state.ErrorKey is null && !loading;
        _status.Text = loading ? Text("Common_Loading")
            : state.ErrorKey is { } error ? Text(error) : null;
    }

    private void SignIn()
    {
        if (!_submit.Enabled)
            return;
        var password = _password.Text ?? string.Empty;
        _password.Text = string.Empty;
        _password.ResignFirstResponder();
        signIn(_email.Text ?? string.Empty, password);
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
