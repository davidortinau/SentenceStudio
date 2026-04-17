using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using Plugin.Maui.HelpKit.Localization;

namespace Plugin.Maui.HelpKit;

/// <summary>
/// Native MAUI chat UI for HelpKit. Built programmatically (no XAML) so the
/// package stays lightweight. Hosts style the page through the
/// <see cref="Ui.HelpKitThemeResources"/> dynamic-resource keys.
/// </summary>
internal sealed class HelpKitPage : ContentPage
{
    private readonly Ui.HelpKitPageViewModel _vm;
    private readonly HelpKitLocalizer _loc;

    private readonly CollectionView _messagesView;
    private readonly Entry _input;
    private readonly Button _sendButton;
    private readonly Button _clearButton;
    private readonly Button _closeButton;

    public HelpKitPage(Ui.HelpKitPageViewModel vm, HelpKitLocalizer loc)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));

        Ui.HelpKitThemeResources.ApplyDefaults(Resources);

        Title = _loc.Get("HelpKit.Title");
        SetDynamicResource(BackgroundColorProperty, Ui.HelpKitThemeResources.SurfaceColor);

        BindingContext = _vm;

        _messagesView = BuildMessagesView();
        _input = BuildInput();
        _sendButton = BuildSendButton();
        _clearButton = BuildClearButton();
        _closeButton = BuildCloseButton();

        Content = BuildLayout();

        _vm.Messages.CollectionChanged += OnMessagesCollectionChanged;
        _vm.MessageAdded += OnMessageAdded;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _input.Focus();
    }

    protected override bool OnBackButtonPressed()
    {
        // Let the presenter handle dismissal through the command so state is
        // cleaned up consistently across hosts.
        _vm.CloseCommand.Execute(null);
        return true;
    }

    // -------------------- layout --------------------

    private Grid BuildLayout()
    {
        var grid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            Padding = new Thickness(0),
            RowSpacing = 0,
        };

        grid.Add(BuildHeader(), 0, 0);
        grid.Add(_messagesView, 0, 1);
        grid.Add(BuildInputBar(), 0, 2);

        return grid;
    }

    private View BuildHeader()
    {
        var title = new Label
        {
            Text = _loc.Get("HelpKit.Title"),
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
        };
        title.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.OnSurface);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);

        var spacer = new BoxView { HorizontalOptions = LayoutOptions.FillAndExpand, Color = Colors.Transparent };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(16, 12),
            ColumnSpacing = 8,
        };
        row.Add(title, 0, 0);
        row.Add(_clearButton, 1, 0);
        row.Add(_closeButton, 2, 0);

        var border = new Border
        {
            StrokeThickness = 0,
            Content = row,
        };
        border.SetDynamicResource(BackgroundColorProperty, Ui.HelpKitThemeResources.SurfaceColor);
        return border;
    }

    private CollectionView BuildMessagesView()
    {
        var cv = new CollectionView
        {
            ItemsSource = _vm.Messages,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new MessageTemplateSelector(_loc),
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepLastItemInView,
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            EmptyView = BuildEmptyView(),
        };
        return cv;
    }

    private View BuildEmptyView()
    {
        var label = new Label
        {
            Text = _loc.Get("HelpKit.EmptyMessage"),
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(32),
            FontSize = 16,
        };
        label.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.MutedText);
        return label;
    }

    private View BuildInputBar()
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            Padding = new Thickness(12, 8, 12, 12),
        };
        row.Add(_input, 0, 0);
        row.Add(_sendButton, 1, 0);

        var border = new Border
        {
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromArgb("#22000000")),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.Rectangle(),
            Content = row,
        };
        border.SetDynamicResource(BackgroundColorProperty, Ui.HelpKitThemeResources.SurfaceColor);
        return border;
    }

    private Entry BuildInput()
    {
        var entry = new Entry
        {
            Placeholder = _loc.Get("HelpKit.Placeholder"),
            ReturnType = ReturnType.Send,
        };
        entry.SetBinding(Entry.TextProperty, new Binding(nameof(Ui.HelpKitPageViewModel.Draft), BindingMode.TwoWay));
        entry.SetBinding(Entry.IsEnabledProperty, new Binding(nameof(Ui.HelpKitPageViewModel.IsStreaming), BindingMode.OneWay, converter: new InvertBoolConverter()));
        entry.Completed += OnInputCompleted;
        SemanticProperties.SetDescription(entry, _loc.Get("HelpKit.InputAccessibility"));
        AutomationProperties.SetName(entry, _loc.Get("HelpKit.InputAccessibility"));
        return entry;
    }

    private Button BuildSendButton()
    {
        var btn = new Button
        {
            Text = _loc.Get("HelpKit.Send"),
        };
        btn.SetDynamicResource(Button.BackgroundColorProperty, Ui.HelpKitThemeResources.PrimaryColor);
        btn.TextColor = Colors.White;
        btn.SetBinding(Button.CommandProperty, new Binding(nameof(Ui.HelpKitPageViewModel.SendCommand)));
        SemanticProperties.SetDescription(btn, _loc.Get("HelpKit.SendAccessibility"));
        AutomationProperties.SetName(btn, _loc.Get("HelpKit.SendAccessibility"));
        return btn;
    }

    private Button BuildClearButton()
    {
        var btn = new Button
        {
            Text = _loc.Get("HelpKit.Clear"),
            BackgroundColor = Colors.Transparent,
        };
        btn.SetDynamicResource(Button.TextColorProperty, Ui.HelpKitThemeResources.OnSurface);
        btn.SetBinding(Button.CommandProperty, new Binding(nameof(Ui.HelpKitPageViewModel.ClearCommand)));
        SemanticProperties.SetDescription(btn, _loc.Get("HelpKit.ClearAccessibility"));
        AutomationProperties.SetName(btn, _loc.Get("HelpKit.ClearAccessibility"));
        return btn;
    }

    private Button BuildCloseButton()
    {
        var btn = new Button
        {
            Text = _loc.Get("HelpKit.Close"),
            BackgroundColor = Colors.Transparent,
        };
        btn.SetDynamicResource(Button.TextColorProperty, Ui.HelpKitThemeResources.OnSurface);
        btn.SetBinding(Button.CommandProperty, new Binding(nameof(Ui.HelpKitPageViewModel.CloseCommand)));
        SemanticProperties.SetDescription(btn, _loc.Get("HelpKit.CloseAccessibility"));
        AutomationProperties.SetName(btn, _loc.Get("HelpKit.CloseAccessibility"));
        return btn;
    }

    // -------------------- events --------------------

    private async void OnInputCompleted(object? sender, EventArgs e)
    {
        if (_vm.SendCommand.CanExecute(null))
        {
            _vm.SendCommand.Execute(null);
            // Return focus to the entry for quick follow-up questions.
            await Dispatcher.DispatchAsync(() => _input.Focus());
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
        {
            var last = e.NewItems[e.NewItems.Count - 1];
            if (last is not null)
            {
                Dispatcher.Dispatch(() =>
                {
                    _messagesView.ScrollTo(last, position: ScrollToPosition.End, animate: true);
                });
            }
        }
    }

    private void OnMessageAdded(object? sender, Ui.HelpKitMessageViewModel vm)
    {
        Dispatcher.Dispatch(() =>
        {
            _messagesView.ScrollTo(vm, position: ScrollToPosition.End, animate: true);
        });
    }

    // -------------------- templates / converters --------------------

    private sealed class InvertBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is bool b ? !b : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is bool b ? !b : value;
    }

    private sealed class MessageTemplateSelector : DataTemplateSelector
    {
        private readonly DataTemplate _user;
        private readonly DataTemplate _assistant;
        private readonly DataTemplate _error;

        public MessageTemplateSelector(HelpKitLocalizer loc)
        {
            _user = new DataTemplate(() => BuildBubble(Ui.HelpKitMessageViewModel.Kind.User, loc));
            _assistant = new DataTemplate(() => BuildBubble(Ui.HelpKitMessageViewModel.Kind.Assistant, loc));
            _error = new DataTemplate(() => BuildBubble(Ui.HelpKitMessageViewModel.Kind.Error, loc));
        }

        protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
        {
            if (item is Ui.HelpKitMessageViewModel vm)
            {
                return vm.MessageKind switch
                {
                    Ui.HelpKitMessageViewModel.Kind.User => _user,
                    Ui.HelpKitMessageViewModel.Kind.Error => _error,
                    _ => _assistant,
                };
            }
            return _assistant;
        }

        private static View BuildBubble(Ui.HelpKitMessageViewModel.Kind kind, HelpKitLocalizer loc)
        {
            var isUser = kind == Ui.HelpKitMessageViewModel.Kind.User;
            var isError = kind == Ui.HelpKitMessageViewModel.Kind.Error;

            var contentLabel = new Label
            {
                LineBreakMode = LineBreakMode.WordWrap,
                FontSize = 15,
            };
            contentLabel.SetBinding(Label.TextProperty, new Binding(nameof(Ui.HelpKitMessageViewModel.Content)));

            if (isUser)
                contentLabel.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.BubbleUserText);
            else if (isError)
                contentLabel.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.ErrorColor);
            else
                contentLabel.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.BubbleAssistantText);

            var stack = new VerticalStackLayout
            {
                Spacing = 4,
                Children = { contentLabel },
            };

            // Citations row (assistant only)
            if (!isUser && !isError)
            {
                var citations = new CollectionView
                {
                    ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 2 },
                    SelectionMode = SelectionMode.None,
                    HeightRequest = 0,
                };
                citations.SetBinding(CollectionView.ItemsSourceProperty, new Binding(nameof(Ui.HelpKitMessageViewModel.CitationsDisplay)));
                citations.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(Ui.HelpKitMessageViewModel.HasCitations)));
                citations.ItemTemplate = new DataTemplate(() =>
                {
                    var cite = new Label { FontSize = 11, LineBreakMode = LineBreakMode.TailTruncation };
                    cite.SetDynamicResource(Label.TextColorProperty, Ui.HelpKitThemeResources.MutedText);
                    cite.SetBinding(Label.TextProperty, new Binding(nameof(HelpKitCitation.SourcePath), stringFormat: "[{0}]"));
                    return cite;
                });

                // Let the CV size to its content.
                citations.HeightRequest = -1;

                stack.Children.Add(new Label
                {
                    Text = loc.Get("HelpKit.CitationsHeader"),
                    FontSize = 10,
                    FontAttributes = FontAttributes.Bold,
                    IsVisible = false, // bound below
                });
                stack.Children[stack.Children.Count - 1].SetBinding(
                    VisualElement.IsVisibleProperty,
                    new Binding(nameof(Ui.HelpKitMessageViewModel.HasCitations)));

                stack.Children.Add(citations);
            }

            var border = new Border
            {
                Padding = new Thickness(12, 8),
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
                Content = stack,
                MaximumWidthRequest = 520,
                HorizontalOptions = isUser ? LayoutOptions.End : LayoutOptions.Start,
            };

            if (isUser)
                border.SetDynamicResource(BackgroundColorProperty, Ui.HelpKitThemeResources.BubbleUser);
            else if (isError)
                border.BackgroundColor = Color.FromArgb("#FEE2E2");
            else
                border.SetDynamicResource(BackgroundColorProperty, Ui.HelpKitThemeResources.BubbleAssistant);

            var wrapper = new Grid
            {
                Padding = new Thickness(12, 4),
                Children = { border },
            };

            // Accessibility: screen readers announce "{role}: {excerpt}".
            AutomationProperties.SetName(wrapper, string.Empty); // set via binding below
            wrapper.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(Ui.HelpKitMessageViewModel.AccessibilityName)));
            SemanticProperties.SetDescription(wrapper, string.Empty);
            wrapper.SetBinding(SemanticProperties.DescriptionProperty, new Binding(nameof(Ui.HelpKitMessageViewModel.AccessibilityName)));

            return wrapper;
        }
    }
}
