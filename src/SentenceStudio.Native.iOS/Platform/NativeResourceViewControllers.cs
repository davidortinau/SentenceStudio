using System.Globalization;
using CoreFoundation;
using Foundation;
using SentenceStudio.Data;
using UIKit;

namespace SentenceStudio.Native.iOS.Platform;

internal sealed class NativeResourceListViewController(
    Func<string, string> text,
    Action onAppear,
    Action<string?> search,
    Action<string?, string?> filter,
    Action<string> open,
    Action synchronize) : UIViewController
{
    private readonly ResourceRows _rows = new(open, text);
    private NativeResourceListState _state = new(NativeResourceListPhase.Idle);
    private UITableView _table = null!;
    private UISearchController _search = null!;
    private UIButton _type = null!;
    private UIButton _language = null!;
    private UIButton _sync = null!;
    private UIButton _retry = null!;
    private UILabel _status = null!;
    private UILabel _count = null!;
    private UILabel _syncStatus = null!;
    private UIActivityIndicatorView _activity = null!;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = NativeVisualTheme.Background;
        View.TintColor = NativeVisualTheme.Primary;
        NavigationItem.Title = text("Resources_Title");

        _search = new UISearchController((UIViewController?)null)
        {
            ObscuresBackgroundDuringPresentation = false,
        };
        _search.SearchBar.Placeholder = text("Resources_SearchPlaceholder");
        _search.SearchBar.AccessibilityIdentifier = "native.resources.search.container";
        _search.SearchBar.SearchTextField.AccessibilityIdentifier = NativeAccess.ResourceSearch;
        _search.SearchBar.TextChanged += (_, _) => search(_search.SearchBar.Text);
        NavigationItem.SearchController = _search;
        NavigationItem.HidesSearchBarWhenScrolling = false;
        DefinesPresentationContext = true;

        _type = FilterButton(NativeAccess.ResourceType);
        _language = FilterButton(NativeAccess.ResourceLanguage);
        var filters = new UIStackView([_type, _language])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 8,
        };
        _count = NativeResourceLayout.Label(string.Empty, UIFontTextStyle.Footnote,
            NativeVisualTheme.SecondaryText, "native.resources.count");
        _status = NativeResourceLayout.Label(string.Empty, UIFontTextStyle.Body,
            NativeVisualTheme.Text, "native.resources.status");
        _syncStatus = NativeResourceLayout.Label(string.Empty, UIFontTextStyle.Footnote,
            NativeVisualTheme.SecondaryText, "native.resources.sync.status");
        _retry = UIButton.FromType(UIButtonType.System);
        _retry.SetTitle(text(NativeText.Retry), UIControlState.Normal);
        _retry.AccessibilityIdentifier = "native.resources.retry";
        _retry.AccessibilityLabel = text(NativeText.Retry);
        _retry.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        _retry.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _retry.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        _retry.TouchUpInside += (_, _) => onAppear();
        _activity = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
        {
            HidesWhenStopped = true,
            Color = NativeVisualTheme.Primary,
        };
        var heading = new UIStackView([filters, _count, _syncStatus, _status, _retry, _activity])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 8,
            LayoutMargins = new UIEdgeInsets(12, NativeVisualTheme.PagePadding,
                8, NativeVisualTheme.PagePadding),
            LayoutMarginsRelativeArrangement = true,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _table = new UITableView
        {
            AccessibilityIdentifier = "native.resources.list",
            BackgroundColor = NativeVisualTheme.Background,
            SeparatorColor = NativeVisualTheme.Border,
            RowHeight = UITableView.AutomaticDimension,
            EstimatedRowHeight = 76,
            Source = _rows,
            TranslatesAutoresizingMaskIntoConstraints = false,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
        };
        View.AddSubviews(heading, _table);
        NSLayoutConstraint.ActivateConstraints(
        [
            heading.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            heading.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            heading.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            _table.TopAnchor.ConstraintEqualTo(heading.BottomAnchor),
            _table.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor),
            _table.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor),
            _table.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor),
        ]);

        _sync = UIButton.FromType(UIButtonType.System);
        _sync.SetTitle(text("NativeResources_Sync"), UIControlState.Normal);
        _sync.AccessibilityIdentifier = "native.resources.sync";
        _sync.AccessibilityLabel = text("NativeResources_Sync");
        _sync.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        _sync.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _sync.TouchUpInside += (_, _) => synchronize();
        _sync.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(_sync);
        Show(_state);
    }

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        onAppear();
    }

    public void Show(NativeResourceListState state)
    {
        _state = state;
        _rows.Items = state.Phase is NativeResourceListPhase.Loading
            or NativeResourceListPhase.LoadError or NativeResourceListPhase.Blocked
            || state.Items.IsDefault ? [] : state.Items;
        if (!IsViewLoaded)
            return;
        NavigationItem.Title = text("Resources_Title");
        _count.Text = string.Format(CultureInfo.CurrentCulture,
            text("Resources_ResourcesCount"), _rows.Items.Length);
        _count.Hidden = state.Phase is NativeResourceListPhase.Loading
            or NativeResourceListPhase.LoadError or NativeResourceListPhase.Blocked;
        _status.Text = state.Phase switch
        {
            NativeResourceListPhase.Empty => text("NativeResources_Empty"),
            NativeResourceListPhase.NoMatches => text("NativeResources_NoMatches"),
            NativeResourceListPhase.LoadError or NativeResourceListPhase.Blocked =>
                text(state.ErrorKey ?? "NativeResources_LoadError"),
            _ => string.Empty
        };
        _status.Hidden = string.IsNullOrEmpty(_status.Text);
        _status.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        _retry.Hidden = state.Phase != NativeResourceListPhase.LoadError;
        var previousSyncStatus = _syncStatus.Text;
        _syncStatus.Text = NativeResourceSyncStatus.Describe(
            state.Sync, state.SyncErrorKey, text);
        _syncStatus.AccessibilityLabel = _syncStatus.Text;
        _syncStatus.Hidden = string.IsNullOrEmpty(_syncStatus.Text);
        var syncActionLabel = state.SyncErrorKey is null
            ? text("NativeResources_Sync") : text(NativeText.Retry);
        _sync.SetTitle(syncActionLabel, UIControlState.Normal);
        _sync.AccessibilityLabel = syncActionLabel;
        _sync.AccessibilityHint = state.SyncErrorKey is null
            ? null : text(state.SyncErrorKey);
        if (state.SyncErrorKey is not null
            && _syncStatus.Text != previousSyncStatus
            && NavigationController?.TopViewController == this
            && UIAccessibility.IsVoiceOverRunning)
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement,
                new NSString(_syncStatus.Text));
        if (state.Phase == NativeResourceListPhase.Loading)
            _activity.StartAnimating();
        else
            _activity.StopAnimating();
        _sync.Enabled = state.Phase is not
            (NativeResourceListPhase.Loading or NativeResourceListPhase.LoadError
                or NativeResourceListPhase.Blocked);
        var canFilter = state.Phase is NativeResourceListPhase.Populated
            or NativeResourceListPhase.Empty or NativeResourceListPhase.NoMatches;
        _search.SearchBar.UserInteractionEnabled = canFilter;
        UpdateFilter(_type, text("NativeResources_TypeFilter"),
            text("Resources_AllTypes"), state.MediaType, state.Types,
            value => filter(value, state.Language));
        UpdateFilter(_language, text("NativeResources_LanguageFilter"),
            text("Resources_AllLanguages"), state.Language, state.Languages,
            value => filter(state.MediaType, value));
        _type.Enabled = canFilter;
        _language.Enabled = canFilter;
        _table.UserInteractionEnabled = canFilter;
        _table.ReloadData();
    }

    public void ShowFailure(string message)
    {
        _status.Text = message;
        _status.Hidden = false;
        _retry.Hidden = false;
        _sync.Enabled = false;
        _search.SearchBar.UserInteractionEnabled = false;
        _type.Enabled = false;
        _language.Enabled = false;
        _table.UserInteractionEnabled = false;
    }

    private static UIButton FilterButton(string id)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.AccessibilityIdentifier = id;
        button.BackgroundColor = NativeVisualTheme.Elevated;
        button.SetTitleColor(NativeVisualTheme.Text, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        button.TitleLabel.AdjustsFontForContentSizeCategory = true;
        button.TitleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        button.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        button.ShowsMenuAsPrimaryAction = true;
        return button;
    }

    private static void UpdateFilter(UIButton button, string label, string all,
        string? current, System.Collections.Immutable.ImmutableArray<string> options,
        Action<string?> selected)
    {
        button.SetTitle(current is null ? all : $"{label}: {current}", UIControlState.Normal);
        button.AccessibilityLabel = label;
        button.AccessibilityValue = current ?? all;
        button.Enabled = !options.IsDefault;
        var actions = new List<UIMenuElement>
        {
            UIAction.Create(all, null, $"{button.AccessibilityIdentifier}.all",
                _ => selected(null))
        };
        if (!options.IsDefault)
            foreach (var option in options)
            {
                var value = option;
                actions.Add(UIAction.Create(value, null,
                    $"{button.AccessibilityIdentifier}.{Uri.EscapeDataString(value)}",
                    _ => selected(value)));
            }
        button.Menu = UIMenu.Create(actions.ToArray());
    }

    private sealed class ResourceRows(
        Action<string> open, Func<string, string> text) : UITableViewSource
    {
        internal System.Collections.Immutable.ImmutableArray<NativeResourceItem> Items = [];

        public override nint RowsInSection(UITableView tableview, nint section) =>
            Items.Length;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
        {
            const string reuseId = "native-resource";
            var cell = tableView.DequeueReusableCell(reuseId)
                ?? new UITableViewCell(UITableViewCellStyle.Subtitle, reuseId);
            var resource = Items[(int)indexPath.Row];
            var title = string.IsNullOrWhiteSpace(resource.Title)
                ? text("NativeResources_Untitled") : resource.Title;
            var metadata = string.Join(" · ", new[] {
                resource.MediaType, resource.Language,
                NativeResourceDateFormatter.ListDate(resource.CreatedAt, resource.IsSmartResource)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            cell.TextLabel!.Text = title;
            cell.TextLabel.Lines = 0;
            cell.TextLabel.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
            cell.TextLabel.AdjustsFontForContentSizeCategory = true;
            cell.TextLabel.TextColor = NativeVisualTheme.Text;
            cell.DetailTextLabel!.Text = metadata;
            cell.DetailTextLabel.Lines = 0;
            cell.DetailTextLabel.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Subheadline);
            cell.DetailTextLabel.AdjustsFontForContentSizeCategory = true;
            cell.DetailTextLabel.TextColor = NativeVisualTheme.SecondaryText;
            cell.BackgroundColor = NativeVisualTheme.Card;
            cell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
            cell.AccessibilityIdentifier = $"{NativeAccess.ResourceRow}.{resource.Id}";
            cell.AccessibilityLabel = $"{title}, {metadata}";
            cell.AccessibilityTraits = UIAccessibilityTrait.Button;
            return cell;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
        {
            tableView.DeselectRow(indexPath, true);
            open(Items[(int)indexPath.Row].Id);
        }
    }
}

internal sealed class NativeResourceDetailViewController(
    Func<string, string> text, Action edit, Action synchronize,
    Action retry) : UIViewController
{
    private NativeResourceDetailState _state = new(NativeResourceDetailPhase.Idle);
    private UIStackView _content = null!;
    private UIButton _edit = null!;
    private UIButton _sync = null!;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _content = NativeResourceLayout.Scroll(View!, keyboard: false);
        _edit = BarButton(text("NativeResources_Edit"), "native.resources.detail.edit", edit);
        _sync = BarButton(text("NativeResources_Sync"), "native.resources.detail.sync", synchronize);
        NavigationItem.RightBarButtonItems =
            [new UIBarButtonItem(_edit), new UIBarButtonItem(_sync)];
        Show(_state);
    }

    public void Show(NativeResourceDetailState state)
    {
        _state = state;
        if (!IsViewLoaded)
            return;
        NativeResourceLayout.Clear(_content);
        NavigationItem.Title = state.Resource?.Title ?? text("Resources_Title");
        _edit.Hidden = state.Resource is not { IsSmartResource: false }
            || state.Phase is NativeResourceDetailPhase.Loading
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.LoadError
                or NativeResourceDetailPhase.Blocked;
        _sync.Enabled = state.Phase is NativeResourceDetailPhase.Ready
            or NativeResourceDetailPhase.ReadOnly;

        if (state.Phase == NativeResourceDetailPhase.Loading)
        {
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                text("Common_Loading"), UIFontTextStyle.Body,
                NativeVisualTheme.Text, "native.resources.detail.status"));
            return;
        }
        if (state.Resource is not { } resource)
        {
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                text(state.ErrorKey ?? "NativeResources_LoadError"),
                UIFontTextStyle.Body, NativeVisualTheme.Text,
                "native.resources.detail.status"));
            var retryButton = BarButton(text(NativeText.Retry),
                "native.resources.detail.retry", retry);
            _content.AddArrangedSubview(retryButton);
            return;
        }

        _content.AddArrangedSubview(NativeResourceLayout.Label(
            string.IsNullOrWhiteSpace(resource.Title)
                ? text("NativeResources_Untitled") : resource.Title,
            UIFontTextStyle.Title2, NativeVisualTheme.Text,
            "native.resources.detail.title", heading: true));
        if (resource.IsSmartResource)
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                $"{text("ResourceEdit_SmartResource")} {text("ResourceEdit_AutoUpdated")}",
                UIFontTextStyle.Body, NativeVisualTheme.Text,
                "native.resources.detail.readonly"));
        if (state.Sync == NativeResourceSyncPhase.Pending)
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                $"{text("DebugHealth_CoreSync")}: {text("DebugHealth_Pending")}",
                UIFontTextStyle.Body, NativeVisualTheme.Text,
                "native.resources.detail.pending"));
        else if (state.Sync == NativeResourceSyncPhase.Confirmed)
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                text("NativeResources_SyncConfirmed"), UIFontTextStyle.Body,
                NativeVisualTheme.Text, "native.resources.detail.confirmed"));
        if (state.SyncErrorKey is not null)
            _content.AddArrangedSubview(NativeResourceLayout.Label(
                text(state.SyncErrorKey), UIFontTextStyle.Body,
                NativeVisualTheme.Text, "native.resources.detail.sync.error"));

        _content.AddArrangedSubview(NativeResourceLayout.Card(
            NativeResourceLayout.Label(text("ResourceEdit_BasicInfo"),
                UIFontTextStyle.Headline, NativeVisualTheme.Text, heading: true),
            NativeResourceLayout.Pair(text("ResourceEdit_Description"),
                resource.Description, text),
            NativeResourceLayout.Pair(text("ResourceEdit_MediaType"),
                resource.MediaType, text),
            NativeResourceLayout.Pair(text("ResourceEdit_Language"),
                resource.Language, text),
            NativeResourceLayout.Pair(text("ResourceEdit_MediaUrl"),
                resource.MediaUrl, text, wrapUrl: true),
            NativeResourceLayout.Pair(text("ResourceEdit_Tags"),
                resource.Tags, text),
            NativeResourceLayout.Pair(text("ResourceEdit_Created"),
                NativeResourceDateFormatter.DetailCreatedDate(
                    resource.CreatedAt, resource.IsSmartResource), text),
            NativeResourceLayout.Pair(text("ResourceEdit_Updated"),
                NativeResourceDateFormatter.DetailUpdatedDate(resource.UpdatedAt), text)));

        var vocab = string.Format(CultureInfo.CurrentCulture,
            text("ResourceEdit_VocabularySection"), resource.VocabularyCount);
        _content.AddArrangedSubview(NativeResourceLayout.Card(
            NativeResourceLayout.Label(vocab, UIFontTextStyle.Headline,
                NativeVisualTheme.Text, "native.resources.detail.vocabulary",
                heading: true)));
        _content.AddArrangedSubview(NativeResourceLayout.Card(
            NativeResourceLayout.Label(
                $"{text("ResourceEdit_Transcript")} · {text("NativeResources_Preview")}",
                UIFontTextStyle.Headline, NativeVisualTheme.Text,
                "native.resources.detail.transcript.title", heading: true),
            NativeResourceLayout.Label(resource.HasTranscript
                    ? resource.TranscriptPreview ?? string.Empty
                    : text("NativeResources_Unavailable"),
                UIFontTextStyle.Body, NativeVisualTheme.Text,
                "native.resources.detail.transcript")));
        _content.AddArrangedSubview(NativeResourceLayout.Card(
            NativeResourceLayout.Label(
                $"{text("ResourceEdit_Translation")} · {text("NativeResources_Preview")}",
                UIFontTextStyle.Headline, NativeVisualTheme.Text,
                "native.resources.detail.translation.title", heading: true),
            NativeResourceLayout.Label(resource.HasTranslation
                    ? resource.TranslationPreview ?? string.Empty
                    : text("NativeResources_Unavailable"),
                UIFontTextStyle.Body, NativeVisualTheme.Text,
                "native.resources.detail.translation")));
    }

    private static UIButton BarButton(string label, string id, Action action)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(label, UIControlState.Normal);
        button.AccessibilityIdentifier = id;
        button.AccessibilityLabel = label;
        button.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        button.TitleLabel.AdjustsFontForContentSizeCategory = true;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        button.TouchUpInside += (_, _) => action();
        return button;
    }
}

internal sealed class NativeResourceEditViewController(
    Func<string, string> text, Action<NativeResourceField, string?> changed,
    Action cancel, Action save, Action reload) : UIViewController
{
    private NativeResourceDetailState _state = new(NativeResourceDetailPhase.Idle);
    private UIStackView _content = null!;
    private UIScrollView _scroll = null!;
    private UITextField _title = null!;
    private UITextView _description = null!;
    private UITextField _tags = null!;
    private UIButton _save = null!;
    private UIButton _cancel = null!;
    private UIButton _reload = null!;
    private UILabel _error = null!;
    private UIActivityIndicatorView _activity = null!;
    private bool _settingDraft;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _content = NativeResourceLayout.Scroll(View!, keyboard: true);
        _scroll = (UIScrollView)_content.Superview!.Superview!;
        NavigationItem.Title = text("NativeResources_Edit");

        _title = new UITextField
        {
            AccessibilityIdentifier = NativeAccess.ResourceTitle,
            AccessibilityLabel = text("ResourceEdit_Title"),
            BorderStyle = UITextBorderStyle.RoundedRect,
            BackgroundColor = NativeVisualTheme.Elevated,
            TextColor = NativeVisualTheme.Text,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true,
            ReturnKeyType = UIReturnKeyType.Next,
            AutocapitalizationType = UITextAutocapitalizationType.Sentences,
        };
        _title.EditingChanged += (_, _) =>
        {
            if (!_settingDraft)
                changed(NativeResourceField.Title, _title.Text);
        };
        _title.EditingDidEndOnExit += (_, _) => _description.BecomeFirstResponder();
        _description = new UITextView
        {
            AccessibilityIdentifier = NativeAccess.ResourceDescription,
            AccessibilityLabel = text("ResourceEdit_Description"),
            BackgroundColor = NativeVisualTheme.Elevated,
            TextColor = NativeVisualTheme.Text,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            ScrollEnabled = false,
            AdjustsFontForContentSizeCategory = true,
        };
        _description.Layer.CornerRadius = NativeVisualTheme.ControlRadius;
        _description.Layer.BorderWidth = 1;
        _description.Layer.BorderColor = NativeVisualTheme.Border.CGColor;
        _description.HeightAnchor.ConstraintGreaterThanOrEqualTo(120).Active = true;
        _description.Changed += (_, _) =>
        {
            if (!_settingDraft)
                changed(NativeResourceField.Description, _description.Text);
            ScrollFocused(_description);
        };
        var next = UIButton.FromType(UIButtonType.System);
        next.SetTitle(text("NativeResources_NextField"), UIControlState.Normal);
        next.AccessibilityLabel = text("NativeResources_NextField");
        next.AccessibilityIdentifier = "native.resources.edit.next";
        next.TouchUpInside += (_, _) => _tags.BecomeFirstResponder();
        var accessory = new UIToolbar();
        accessory.SizeToFit();
        accessory.Items = [
            new UIBarButtonItem(UIBarButtonSystemItem.FlexibleSpace),
            new UIBarButtonItem(next)
        ];
        _description.InputAccessoryView = accessory;

        _tags = new UITextField
        {
            AccessibilityIdentifier = NativeAccess.ResourceTags,
            AccessibilityLabel = text("ResourceEdit_Tags"),
            BorderStyle = UITextBorderStyle.RoundedRect,
            BackgroundColor = NativeVisualTheme.Elevated,
            TextColor = NativeVisualTheme.Text,
            Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body),
            AdjustsFontForContentSizeCategory = true,
            ReturnKeyType = UIReturnKeyType.Done,
        };
        _tags.EditingChanged += (_, _) =>
        {
            if (!_settingDraft)
                changed(NativeResourceField.Tags, _tags.Text);
        };
        _tags.EditingDidEndOnExit += (_, _) => _tags.ResignFirstResponder();
        _title.EditingDidBegin += (_, _) => ScrollFocused(_title);
        _tags.EditingDidBegin += (_, _) => ScrollFocused(_tags);

        _error = NativeResourceLayout.Label(string.Empty, UIFontTextStyle.Body,
            NativeVisualTheme.Text, "native.resources.edit.error");
        _error.Hidden = true;
        _reload = UIButton.FromType(UIButtonType.System);
        _reload.SetTitle(text("NativeResources_ReloadDiscard"), UIControlState.Normal);
        _reload.AccessibilityIdentifier = NativeAccess.ResourceReload;
        _reload.AccessibilityLabel = text("NativeResources_ReloadDiscard");
        _reload.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        _reload.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _reload.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        _reload.TouchUpInside += (_, _) => reload();
        _reload.Hidden = true;
        _activity = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
        {
            HidesWhenStopped = true,
            Color = NativeVisualTheme.Primary,
        };
        _content.AddArrangedSubview(NativeResourceLayout.Label(
            text("ResourceEdit_Title"), UIFontTextStyle.Headline,
            NativeVisualTheme.Text, heading: true));
        _content.AddArrangedSubview(_title);
        _title.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        _content.AddArrangedSubview(NativeResourceLayout.Label(
            text("ResourceEdit_Description"), UIFontTextStyle.Headline,
            NativeVisualTheme.Text, heading: true));
        _content.AddArrangedSubview(_description);
        _content.AddArrangedSubview(NativeResourceLayout.Label(
            text("ResourceEdit_Tags"), UIFontTextStyle.Headline,
            NativeVisualTheme.Text, heading: true));
        _content.AddArrangedSubview(_tags);
        _tags.HeightAnchor.ConstraintGreaterThanOrEqualTo(
            NativeVisualTheme.ControlHeight).Active = true;
        _content.AddArrangedSubview(_error);
        _content.AddArrangedSubview(_reload);
        _content.AddArrangedSubview(_activity);

        _save = UIButton.FromType(UIButtonType.System);
        _save.SetTitle(text("Common_Save"), UIControlState.Normal);
        _save.AccessibilityIdentifier = NativeAccess.ResourceSave;
        _save.AccessibilityLabel = text("Common_Save");
        _save.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Headline);
        _save.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _save.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        _save.TouchUpInside += (_, _) => save();
        _cancel = UIButton.FromType(UIButtonType.System);
        _cancel.SetTitle(text("Common_Cancel"), UIControlState.Normal);
        _cancel.AccessibilityIdentifier = NativeAccess.ResourceCancel;
        _cancel.AccessibilityLabel = text("Common_Cancel");
        _cancel.TitleLabel!.Font = UIFont.GetPreferredFontForTextStyle(UIFontTextStyle.Body);
        _cancel.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _cancel.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        _cancel.TouchUpInside += (_, _) => cancel();
        NavigationItem.RightBarButtonItems =
            [new UIBarButtonItem(_save), new UIBarButtonItem(_cancel)];
        Show(_state);
    }

    public void SetDraft(NativeResourceDetailState state)
    {
        if (state.Draft is not { } draft)
            throw new InvalidOperationException("Resource editor requires an isolated draft.");
        LoadViewIfNeeded();
        _settingDraft = true;
        try
        {
            _title.Text = draft.Title;
            _description.Text = draft.Description;
            _tags.Text = draft.Tags;
        }
        finally
        {
            _settingDraft = false;
        }
        Show(state);
    }

    public void CommitComposingText(NativeResourceDraft draft)
    {
        View!.EndEditing(true);
        if (!string.Equals(_title.Text ?? string.Empty, draft.Title, StringComparison.Ordinal))
            changed(NativeResourceField.Title, _title.Text);
        if (!string.Equals(_description.Text ?? string.Empty,
                draft.Description ?? string.Empty, StringComparison.Ordinal))
            changed(NativeResourceField.Description, _description.Text);
        if (!string.Equals(_tags.Text ?? string.Empty,
                draft.Tags ?? string.Empty, StringComparison.Ordinal))
            changed(NativeResourceField.Tags, _tags.Text);
    }

    public void Show(NativeResourceDetailState state)
    {
        var wasConflicted = _state.Phase == NativeResourceDetailPhase.Conflict;
        _state = state;
        if (!IsViewLoaded)
            return;
        var busy = state.Phase == NativeResourceDetailPhase.Saving || state.Reloading;
        NavigationItem.HidesBackButton = busy;
        _title.Enabled = !busy;
        _description.Editable = !busy;
        _tags.Enabled = !busy;
        _save.Enabled = !busy && state.Draft is not null
            && state.Phase is (NativeResourceDetailPhase.Ready
                or NativeResourceDetailPhase.SaveError)
            && state.LastSaveResult != ResourceMetadataUpdateResult.ValidationFailed;
        _cancel.Enabled = !busy;
        _reload.Hidden = state.Draft is null
            || state.Phase is not (NativeResourceDetailPhase.Conflict
                or NativeResourceDetailPhase.NotFound or NativeResourceDetailPhase.ReadOnly)
                && state.LastSaveResult != ResourceMetadataUpdateResult.ValidationFailed;
        _reload.Enabled = !busy;
        if (busy)
            _activity.StartAnimating();
        else
            _activity.StopAnimating();
        _error.Text = state.ErrorKey is null ? string.Empty : text(state.ErrorKey);
        _error.Hidden = string.IsNullOrEmpty(_error.Text);
        _error.AccessibilityIdentifier = state.Phase == NativeResourceDetailPhase.Conflict
            ? NativeAccess.ResourceConflict : "native.resources.edit.error";
        if (state.Phase == NativeResourceDetailPhase.Conflict && !wasConflicted
            && View?.Window is not null && PresentedViewController is null)
        {
            var alert = UIAlertController.Create(text("NativeResources_ConflictTitle"),
                text("NativeResources_Conflict"), UIAlertControllerStyle.Alert);
            alert.View.AccessibilityIdentifier = NativeAccess.ResourceConflictAlert;
            alert.AddAction(UIAlertAction.Create(text("NativeResources_ReloadDiscard"),
                UIAlertActionStyle.Default, _ => reload()));
            alert.AddAction(UIAlertAction.Create(text("NativeResources_KeepEditing"),
                UIAlertActionStyle.Cancel, null));
            PresentViewController(alert, true, null);
        }
    }

    private void ScrollFocused(UIView input) =>
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            if (!IsViewLoaded || !input.IsFirstResponder)
                return;
            View!.LayoutIfNeeded();
            _scroll.ScrollRectToVisible(input.ConvertRectToView(input.Bounds, _scroll),
                true);
        });
}

internal static class NativeResourceLayout
{
    internal static UIStackView Scroll(UIView root, bool keyboard)
    {
        root.BackgroundColor = NativeVisualTheme.Background;
        root.TintColor = NativeVisualTheme.Primary;
        var scroll = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            BackgroundColor = NativeVisualTheme.Background,
        };
        var container = new UIView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            LayoutMargins = new UIEdgeInsets(0, NativeVisualTheme.PagePadding,
                0, NativeVisualTheme.PagePadding),
        };
        var content = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        root.AddSubview(scroll);
        scroll.AddSubview(container);
        container.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            scroll.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(keyboard
                ? root.KeyboardLayoutGuide.TopAnchor
                : root.SafeAreaLayoutGuide.BottomAnchor),
            container.TopAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TopAnchor),
            container.BottomAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.BottomAnchor),
            container.LeadingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.LeadingAnchor),
            container.TrailingAnchor.ConstraintEqualTo(scroll.ContentLayoutGuide.TrailingAnchor),
            container.WidthAnchor.ConstraintEqualTo(scroll.FrameLayoutGuide.WidthAnchor),
            content.TopAnchor.ConstraintEqualTo(container.TopAnchor, 16),
            content.BottomAnchor.ConstraintEqualTo(container.BottomAnchor, -24),
            content.LeadingAnchor.ConstraintEqualTo(container.ReadableContentGuide.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(container.ReadableContentGuide.TrailingAnchor),
        ]);
        return content;
    }

    internal static UILabel Label(string value, UIFontTextStyle style,
        UIColor color, string? id = null, bool heading = false)
    {
        var label = new UILabel
        {
            Text = value,
            TextColor = color,
            Font = UIFont.GetPreferredFontForTextStyle(style),
            AdjustsFontForContentSizeCategory = true,
            Lines = 0,
            AccessibilityIdentifier = id,
            AccessibilityTraits = heading
                ? UIAccessibilityTrait.Header : UIAccessibilityTrait.StaticText,
        };
        return label;
    }

    internal static UIView Pair(string heading, string? value,
        Func<string, string> text, bool wrapUrl = false)
    {
        var label = Label(heading, UIFontTextStyle.Subheadline,
            NativeVisualTheme.SecondaryText, heading: true);
        var content = Label(string.IsNullOrWhiteSpace(value)
                ? text("NativeResources_Unavailable") : value,
            UIFontTextStyle.Body, NativeVisualTheme.Text);
        if (wrapUrl)
            content.LineBreakMode = UILineBreakMode.CharacterWrap;
        return new UIStackView([label, content])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 2,
        };
    }

    internal static UIView Card(params UIView[] children)
    {
        var content = new UIStackView(children)
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 10,
            LayoutMargins = new UIEdgeInsets(NativeVisualTheme.CardPadding,
                NativeVisualTheme.CardPadding, NativeVisualTheme.CardPadding,
                NativeVisualTheme.CardPadding),
            LayoutMarginsRelativeArrangement = true,
            BackgroundColor = NativeVisualTheme.Card,
        };
        content.Layer.CornerRadius = NativeVisualTheme.CardRadius;
        return content;
    }

    internal static void Clear(UIStackView content)
    {
        foreach (var child in content.ArrangedSubviews)
        {
            content.RemoveArrangedSubview(child);
            child.RemoveFromSuperview();
            child.Dispose();
        }
    }
}
