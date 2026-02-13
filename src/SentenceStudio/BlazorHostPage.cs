using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;
using SentenceStudio.Services;

namespace SentenceStudio;

/// <summary>
/// MAUI ContentPage that hosts the BlazorWebView.
/// On macOS (Mac Catalyst), includes a native footer for the Translation page
/// to work around Korean IME cursor-jumping in WKWebView.
/// Other platforms use the Blazor input bar directly.
/// </summary>
public class BlazorHostPage : Microsoft.Maui.Controls.ContentPage
{
    private static bool IsMacCatalyst => OperatingSystem.IsMacCatalyst();

    private TranslationBridge? _bridge;
    private Grid? _footerGrid;
    private Entry? _inputEntry;
    private Border? _inputBorder;
    private Button? _gradeButton;
    private Button? _toggleButton;
    private Button? _prevButton;
    private Button? _nextButton;
    private Label? _progressLabel;
    private FlexLayout? _vocabBlocksLayout;
    private BoxView? _separator;

    private string _inputMode = "Text";
    private bool _isGrading;

    public BlazorHostPage()
    {
        var blazorWebView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html"
        };
        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(WebUI.Routes)
        });

        if (IsMacCatalyst)
        {
            // Build the native footer (colors applied later in OnHandlerChanged)
            BuildFooter();

            var layout = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                RowSpacing = 0
            };
            layout.Add(blazorWebView, 0, 0);
            layout.Add(_separator!, 0, 1);
            layout.Add(_footerGrid!, 0, 2);
            Content = layout;
        }
        else
        {
            Content = blazorWebView;
        }
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();

        if (!IsMacCatalyst) return;

        // Resolve DI bridge now that Handler is available
        _bridge = Handler?.MauiContext?.Services.GetService<TranslationBridge>();
        if (_bridge != null)
            SubscribeToBridge(_bridge);

        // Apply theme colors now that the platform is fully initialized
        ApplyThemeColors();

        // Listen for runtime theme changes
        if (Application.Current != null)
            Application.Current.RequestedThemeChanged += OnRequestedThemeChanged;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(ApplyThemeColors);
    }

    private void SubscribeToBridge(TranslationBridge bridge)
    {
        bridge.OnProgressChanged += OnProgressChanged;
        bridge.OnGradingStateChanged += OnGradingStateChanged;
        bridge.OnVocabBlocksChanged += OnVocabBlocksChanged;
        bridge.OnInputModeChanged += OnInputModeChanged;
        bridge.OnCanGoPreviousChanged += OnCanGoPreviousChanged;
        bridge.OnInputClearRequested += OnInputClearRequested;
        bridge.OnContentReadyChanged += OnContentReadyChanged;
        bridge.OnVocabBlockAppend += OnVocabBlockAppend;
    }

    private void ApplyThemeColors()
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

        var bgColor = isDark ? Color.FromArgb("#060913") : Colors.White;
        var borderColor = isDark ? Color.FromArgb("#263457") : Color.FromArgb("#dee2e6");
        var textColor = isDark ? Color.FromArgb("#F8FAFF") : Color.FromArgb("#212529");
        var primaryColor = isDark ? Color.FromArgb("#6B8CFF") : Color.FromArgb("#2c3e50");
        var onPrimaryColor = isDark ? Color.FromArgb("#081022") : Colors.White;
        var surfaceVariant = isDark ? Color.FromArgb("#121B33") : Color.FromArgb("#f8f9fa");
        var mutedTextColor = isDark ? Color.FromArgb("#C6D0E7") : Color.FromArgb("#6c757d");

        if (_footerGrid != null) _footerGrid.BackgroundColor = bgColor;
        if (_separator != null) _separator.Color = borderColor;

        if (_inputEntry != null)
        {
            _inputEntry.BackgroundColor = Colors.Transparent;
            _inputEntry.TextColor = textColor;
            _inputEntry.PlaceholderColor = mutedTextColor;
        }

        if (_inputBorder != null)
        {
            _inputBorder.Stroke = borderColor;
            _inputBorder.BackgroundColor = bgColor;
        }

        if (_gradeButton != null)
        {
            _gradeButton.BackgroundColor = primaryColor;
            _gradeButton.TextColor = onPrimaryColor;
        }

        if (_toggleButton != null)
        {
            _toggleButton.BackgroundColor = surfaceVariant;
            _toggleButton.TextColor = textColor;
            _toggleButton.BorderColor = borderColor;
        }

        if (_prevButton != null)
        {
            _prevButton.BackgroundColor = surfaceVariant;
            _prevButton.TextColor = textColor;
            _prevButton.BorderColor = borderColor;
        }

        if (_nextButton != null)
        {
            _nextButton.BackgroundColor = surfaceVariant;
            _nextButton.TextColor = textColor;
            _nextButton.BorderColor = borderColor;
        }

        if (_progressLabel != null) _progressLabel.TextColor = mutedTextColor;
    }

    private void BuildFooter()
    {
        _inputEntry = new Entry
        {
            Placeholder = "Translate to Korean...",
            FontSize = 16,
            MinimumHeightRequest = 44,
            HorizontalOptions = LayoutOptions.Fill,
            ReturnType = ReturnType.Done
        };
        _inputEntry.Completed += OnEntryCompleted;

        // Wrap Entry in a Border to match Blazor .form-control-ss styling
        _inputBorder = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            StrokeThickness = 1,
            Padding = new Thickness(8, 0),
            Content = _inputEntry
        };

        _gradeButton = new Button
        {
            Text = "Grade",
            FontSize = 14,
            MinimumHeightRequest = 44,
            MinimumWidthRequest = 44,
            Padding = new Thickness(14, 10),
            CornerRadius = 6
        };
        _gradeButton.Clicked += OnGradeClicked;

        _toggleButton = new Button
        {
            Text = "Blocks",
            FontSize = 14,
            MinimumHeightRequest = 44,
            MinimumWidthRequest = 44,
            Padding = new Thickness(14, 10),
            CornerRadius = 6,
            BorderWidth = 1
        };
        _toggleButton.Clicked += OnToggleClicked;

        _prevButton = new Button
        {
            Text = "‹",
            FontSize = 18,
            MinimumHeightRequest = 36,
            MinimumWidthRequest = 44,
            Padding = new Thickness(14, 6),
            CornerRadius = 6,
            BorderWidth = 1,
            IsEnabled = false
        };
        _prevButton.Clicked += (s, e) => _bridge?.RequestPrevious();

        _nextButton = new Button
        {
            Text = "›",
            FontSize = 18,
            MinimumHeightRequest = 36,
            MinimumWidthRequest = 44,
            Padding = new Thickness(14, 6),
            CornerRadius = 6,
            BorderWidth = 1
        };
        _nextButton.Clicked += (s, e) => _bridge?.RequestNext();

        _progressLabel = new Label
        {
            Text = "1 / 5",
            FontSize = 14,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        _vocabBlocksLayout = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 0)
        };

        // Input row: Entry + Toggle + Grade
        var inputRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 8)
        };
        inputRow.Add(_inputBorder, 0);
        inputRow.Add(_toggleButton, 1);
        inputRow.Add(_gradeButton, 2);

        // Nav row: < progress >
        var navRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8
        };
        navRow.Add(_prevButton, 0);
        navRow.Add(_progressLabel, 1);
        navRow.Add(_nextButton, 2);

        _separator = new BoxView
        {
            HeightRequest = 1,
            HorizontalOptions = LayoutOptions.Fill,
            IsVisible = false
        };

        _footerGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), // vocab blocks
                new RowDefinition(GridLength.Auto), // input row
                new RowDefinition(GridLength.Auto)  // nav row
            },
            Padding = new Thickness(16, 12, 16, 12),
            IsVisible = false // hidden until Translation page signals ready
        };
        _footerGrid.Add(_vocabBlocksLayout, 0, 0);
        _footerGrid.Add(inputRow, 0, 1);
        _footerGrid.Add(navRow, 0, 2);
    }

    // ── Entry/Button event handlers ──

    private void OnEntryCompleted(object? sender, EventArgs e)
    {
        if (!_isGrading)
            _bridge?.RequestGrade(_inputEntry?.Text ?? "");
    }

    private void OnGradeClicked(object? sender, EventArgs e)
    {
        if (!_isGrading)
            _bridge?.RequestGrade(_inputEntry?.Text ?? "");
    }

    private void OnToggleClicked(object? sender, EventArgs e)
    {
        _bridge?.RequestToggleInputMode();
    }

    // ── Bridge → Native event handlers ──

    private void OnProgressChanged(int current, int total)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_progressLabel != null) _progressLabel.Text = $"{current} / {total}";
        });
    }

    private void OnGradingStateChanged(bool isGrading)
    {
        _isGrading = isGrading;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_gradeButton != null)
            {
                _gradeButton.IsEnabled = !isGrading;
                _gradeButton.Text = isGrading ? "..." : "Grade";
            }
        });
    }

    private void OnVocabBlocksChanged(List<string> blocks)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_vocabBlocksLayout == null || _inputEntry == null) return;

            _vocabBlocksLayout.Children.Clear();
            var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
            var borderColor = isDark ? Color.FromArgb("#263457") : Color.FromArgb("#dee2e6");
            var mutedTextColor = isDark ? Color.FromArgb("#C6D0E7") : Color.FromArgb("#6c757d");

            if (_inputMode == "MultipleChoice" && blocks.Any())
            {
                foreach (var word in blocks)
                {
                    var btn = new Button
                    {
                        Text = word,
                        BackgroundColor = Colors.Transparent,
                        TextColor = mutedTextColor,
                        FontSize = 14,
                        Padding = new Thickness(10, 6),
                        CornerRadius = 6,
                        BorderColor = borderColor,
                        BorderWidth = 1,
                        MinimumHeightRequest = 32,
                        Margin = new Thickness(0, 0, 8, 8)
                    };
                    var w = word;
                    btn.Clicked += (s, e) =>
                    {
                        var current = _inputEntry.Text ?? "";
                        _inputEntry.Text = string.IsNullOrEmpty(current) ? w : $"{current} {w}";
                    };
                    _vocabBlocksLayout.Children.Add(btn);
                }
                _vocabBlocksLayout.IsVisible = true;
            }
            else
            {
                _vocabBlocksLayout.IsVisible = false;
            }
        });
    }

    private void OnInputModeChanged(string mode)
    {
        _inputMode = mode;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_toggleButton != null) _toggleButton.Text = mode == "Text" ? "Blocks" : "Type";
        });
    }

    private void OnCanGoPreviousChanged(bool canGo)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_prevButton != null) _prevButton.IsEnabled = canGo;
        });
    }

    private void OnInputClearRequested()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_inputEntry != null) _inputEntry.Text = "";
        });
    }

    private void OnContentReadyChanged(bool ready)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_footerGrid != null) _footerGrid.IsVisible = ready;
            if (_separator != null) _separator.IsVisible = ready;
        });
    }

    private void OnVocabBlockAppend(string word)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_inputEntry == null) return;
            var current = _inputEntry.Text ?? "";
            _inputEntry.Text = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
        });
    }
}
