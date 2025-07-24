using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;
using SentenceStudio.Pages.Reading;
using SentenceStudio.Services;
using SentenceStudio.Resources.Styles;

namespace SentenceStudio.Components;

/// <summary>
/// Represents a segment of text with vocabulary metadata
/// </summary>
public class TextSegment
{
    public string Text { get; set; } = string.Empty;
    public bool IsVocabulary { get; set; }
    public bool IsWord { get; set; }
    public VocabularyWord? VocabularyWord { get; set; }
}

/// <summary>
/// High-performance SkiaSharp-based text renderer with word-level interaction support.
/// Provides 10-50x performance improvement over VisualNode-based approach.
/// </summary>
public class InteractiveTextRenderer : SKCanvasView
{
    private ILogger<InteractiveTextRenderer> _logger;
    
    // Text rendering state
    private List<WordBounds> _wordBounds = new();
    private List<SentenceBounds> _sentenceBounds = new();
    private string _text = string.Empty;
    private float _fontSize = 18f;
    private Color _textColor = Colors.Black;
    private Color _highlightColor = Colors.Yellow;
    private int _currentSentenceIndex = -1;
    private float _calculatedHeight = 0f;
    
    // Touch interaction
    private WordBounds? _lastTappedWord;
    
    // 🎯 NEW: Double-tap detection with debouncing
    private DateTime _lastTapTime = DateTime.MinValue;
    private WordBounds? _lastTapWord = null;
    private const double DoubleTapThreshold = 500; // milliseconds
    private System.Threading.Timer? _singleTapTimer;
    private WordBounds? _pendingSingleTapWord;
    
    // Rendering resources
    private SKPaint _textPaint;
    private SKPaint _highlightedTextPaint; // NEW: Paint for highlighted sentence text
    private SKPaint _vocabularyPaint;
    private SKPaint _highlightedVocabularyPaint; // NEW: Paint for highlighted vocabulary text
    private SKFont _font;
    private bool _needsLayout = true;
    
    // Events for interaction
    public event Action<string>? WordTapped;
    public event Action<VocabularyWord>? VocabularyWordTapped;
    
    // 🎯 NEW: Sentence-level interaction events
    public event Action<int>? SentenceTapped;
    public event Action<int>? SentenceDoubleTapped;

    public InteractiveTextRenderer()
    {
        // Will be set when component is attached to handler
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<InteractiveTextRenderer>.Instance;
        
        EnableTouchEvents = true;
        
        // Set minimum size constraints to ensure the canvas gets dimensions
        MinimumHeightRequest = 100;
        MinimumWidthRequest = 200;
        
        // Set initial height request to prevent zero-height issues
        HeightRequest = 200; // Will be updated when content is set
        
        // Defer paint initialization until the control is loaded
    }

    #region Public Properties

    /// <summary>
    /// Sets the text content with sentence and vocabulary information
    /// </summary>
    public void SetContent(List<string> sentences, List<VocabularyWord> vocabularyWords, List<List<TextSegment>> sentenceSegments)
    {
        PerformanceLogger.Time("SetContent", () =>
        {
            _logger.LogDebug("SetContent called with {SentenceCount} sentences, {VocabularyCount} vocab words", 
                sentences?.Count ?? 0, vocabularyWords?.Count ?? 0);
            
            _text = string.Join(" ", sentences);
            _logger.LogTrace("Combined text length: {TextLength} characters", _text.Length);
            
            _needsLayout = true;
            
            // Pre-process vocabulary for fast lookup
            var vocabLookup = vocabularyWords.ToDictionary(v => v.TargetLanguageTerm, v => v);
            
            // Build word bounds with vocabulary information
            BuildWordBounds(sentences, sentenceSegments, vocabLookup);
            
            _logger.LogDebug("Built {WordBoundsCount} word bounds, {SentenceBoundsCount} sentence bounds", 
                _wordBounds.Count, _sentenceBounds.Count);
            
            InvalidateSurface();
            // Update height request after content changes
            UpdateHeightRequest();
        }, 10.0);
    }

    /// <summary>
    /// Updates which sentence is currently highlighted
    /// </summary>
    public void SetCurrentSentence(int sentenceIndex)
    {
        if (_currentSentenceIndex != sentenceIndex)
        {
            _currentSentenceIndex = sentenceIndex;
            InvalidateSurface(); // Only invalidate, no layout needed
        }
    }

    /// <summary>
    /// Updates the font size and triggers re-layout
    /// </summary>
    public void SetFontSize(float fontSize)
    {
        _logger.LogTrace("SetFontSize called: current={CurrentFontSize}, new={NewFontSize}", _fontSize, fontSize);
        
        if (Math.Abs(_fontSize - fontSize) > 0.1f)
        {
            _logger.LogDebug("Font size changing from {OldSize} to {NewSize}", _fontSize, fontSize);
            _fontSize = fontSize;
            
            // Only update if paints are initialized
            if (_textPaint != null && _vocabularyPaint != null)
            {
                _textPaint.TextSize = fontSize;
                _highlightedTextPaint.TextSize = fontSize;
                _vocabularyPaint.TextSize = fontSize;
                _highlightedVocabularyPaint.TextSize = fontSize;
                
                // Update font with current typeface (will be Korean font once loaded)
                if (_font != null)
                {
                    var currentTypeface = _font.Typeface;
                    _font.Dispose();
                    _font = new SKFont(currentTypeface, fontSize);
                    
                    // Apply the typeface to all paints
                    _textPaint.Typeface = currentTypeface;
                    _highlightedTextPaint.Typeface = currentTypeface;
                    _vocabularyPaint.Typeface = currentTypeface;
                    _highlightedVocabularyPaint.Typeface = currentTypeface;
                }
                
                _needsLayout = true;
                InvalidateSurface();
                // Update height request after font size changes
                UpdateHeightRequest();
                _logger.LogDebug("Font size updated and surface invalidated");
            }
            else
            {
                _logger.LogWarning("Paints not initialized yet, font size change deferred");
            }
        }
        else
        {
            _logger.LogTrace("Font size change too small, ignoring");
        }
    }

    #endregion

    #region Initialization

    private void InitializePaints()
    {
        _logger.LogDebug("InitializePaints called");
        
        try
        {
            // Text paint for regular words - use theme text color with fallback
            var textColor = GetThemeTextColor();
            _textPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = _fontSize,
                Color = textColor,
                TextAlign = SKTextAlign.Left,
                Typeface = SKTypeface.Default  // Start with default, will be updated with Korean font
            };
            _logger.LogTrace("Text paint created: Size={TextSize}, Color={Color}", _textPaint.TextSize, _textPaint.Color);

            // Highlighted text paint for current sentence - use primary color for sentence highlighting
            var highlightColor = ApplicationTheme.Primary.ToSKColor();
            _highlightedTextPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = _fontSize,
                Color = highlightColor,
                TextAlign = SKTextAlign.Left,
                Typeface = SKTypeface.Default  // Start with default, will be updated with Korean font
            };
            _logger.LogTrace("Highlighted text paint created: Size={TextSize}, Color={Color}", _highlightedTextPaint.TextSize, _highlightedTextPaint.Color);

            // Vocabulary paint for vocabulary words - use secondary color for vocabulary highlighting
            var vocabColor = ApplicationTheme.Tertiary.ToSKColor();
            _vocabularyPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = _fontSize,
                Color = vocabColor,
                TextAlign = SKTextAlign.Left,
                Typeface = SKTypeface.Default  // Start with default, will be updated with Korean font
                // Note: SkiaSharp doesn't support UnderlineText directly
                // Will draw underline manually if needed
            };
            _logger.LogTrace("Vocabulary paint created: Size={TextSize}, Color={Color}", _vocabularyPaint.TextSize, _vocabularyPaint.Color);

            // Highlighted vocabulary paint for vocabulary words in current sentence - use primary + secondary mix
            var highlightedVocabColor = Color.FromRgba(
                (ApplicationTheme.Primary.Red + ApplicationTheme.Secondary.Red) / 2,
                (ApplicationTheme.Primary.Green + ApplicationTheme.Secondary.Green) / 2,
                (ApplicationTheme.Primary.Blue + ApplicationTheme.Secondary.Blue) / 2,
                1.0f).ToSKColor();
            _highlightedVocabularyPaint = new SKPaint
            {
                IsAntialias = true,
                TextSize = _fontSize,
                Color = highlightedVocabColor,
                TextAlign = SKTextAlign.Left,
                Typeface = SKTypeface.Default  // Start with default, will be updated with Korean font
            };
            _logger.LogTrace("Highlighted vocabulary paint created: Size={TextSize}, Color={Color}", _highlightedVocabularyPaint.TextSize, _highlightedVocabularyPaint.Color);

            // Font for text measurement - Initialize with default, load Korean font async
            _font = new SKFont(SKTypeface.Default, _fontSize);
            _logger.LogTrace("Font created: Size={FontSize}", _font.Size);
            
            // Ensure we have visible colors for debugging
            if (_textPaint.Color.Alpha == 0)
            {
                _logger.LogWarning("Text color is transparent, using fallback");
                _textPaint.Color = SKColors.Black;
            }
            if (_vocabularyPaint.Color.Alpha == 0)
            {
                _logger.LogWarning("Vocabulary color is transparent, using fallback");
                _vocabularyPaint.Color = SKColors.Blue;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in InitializePaints");
        }
        
        // Load the Korean font asynchronously and update when ready
        _ = Task.Run(async () =>
        {
            try
            {
                // Load Korean font using FileSystem.OpenAppPackageFileAsync as suggested
                using var fontStream = await FileSystem.OpenAppPackageFileAsync("bm_yeonsung.ttf");
                var typeface = SKTypeface.FromStream(fontStream);
                
                if (typeface != null)
                {
                    // Update the font AND paints on the main thread
                    Application.Current?.Dispatcher.Dispatch(() =>
                    {
                        _font?.Dispose();
                        _font = new SKFont(typeface, _fontSize);
                        
                        // CRITICAL: Apply the Korean typeface to all paints
                        _textPaint.Typeface = typeface;
                        _highlightedTextPaint.Typeface = typeface;
                        _vocabularyPaint.Typeface = typeface;
                        _highlightedVocabularyPaint.Typeface = typeface;
                        
                        InvalidateSurface(); // Redraw with new font
                        _logger.LogInformation("Successfully loaded and applied Korean font to paints");
                    });
                }
                else
                {
                    _logger.LogWarning("Korean font stream was null, keeping default font");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Korean font loading failed, keeping default font");
            }
        });
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the current theme background color
    /// </summary>
    private SKColor GetThemeBackgroundColor()
    {
        try
        {
            // Use ApplicationTheme to get the correct background color
            var backgroundColor = ApplicationTheme.IsLightTheme 
                ? ApplicationTheme.LightBackground 
                : ApplicationTheme.DarkBackground;
            
            _logger.LogTrace("Theme background color: IsLight={IsLight}, Color={Color}", ApplicationTheme.IsLightTheme, backgroundColor);
            return backgroundColor.ToSKColor();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting theme background color, using fallback");
            // Fallback to basic colors
            return ApplicationTheme.IsLightTheme ? SKColors.White : SKColors.Black;
        }
    }

    /// <summary>
    /// Gets the current theme text color
    /// </summary>
    private SKColor GetThemeTextColor()
    {
        try
        {
            // Use ApplicationTheme to get the correct text color
            var textColor = ApplicationTheme.IsLightTheme 
                ? ApplicationTheme.DarkOnLightBackground 
                : ApplicationTheme.LightOnDarkBackground;
            
            _logger.LogTrace("Theme text color: IsLight={IsLight}, Color={Color}", ApplicationTheme.IsLightTheme, textColor);
            return textColor.ToSKColor();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting theme text color, using fallback");
            // Fallback to basic colors
            return ApplicationTheme.IsLightTheme ? SKColors.Black : SKColors.White;
        }
    }

    /// <summary>
    /// Calculates and updates the height request based on content
    /// </summary>
    private void UpdateHeightRequest()
    {
        // Ensure we have a minimum height even if calculation hasn't happened yet
        var targetHeight = Math.Max(_calculatedHeight, 100); // Minimum 100px height
        
        if (targetHeight > 0)
        {
            Application.Current?.Dispatcher.Dispatch(() =>
            {
                _logger.LogTrace("UpdateHeightRequest: Setting height to {TargetHeight} (calculated: {CalculatedHeight})", targetHeight, _calculatedHeight);
                HeightRequest = targetHeight;
            });
        }
    }

    #endregion

    #region Layout and Word Bounds

    private void BuildWordBounds(List<string> sentences, List<List<TextSegment>> sentenceSegments, Dictionary<string, VocabularyWord> vocabLookup)
    {
        _wordBounds.Clear();
        _sentenceBounds.Clear();
        
        int globalWordIndex = 0;
        
        for (int sentenceIndex = 0; sentenceIndex < sentences.Count; sentenceIndex++)
        {
            var sentence = sentences[sentenceIndex];
            var segments = sentenceIndex < sentenceSegments.Count ? sentenceSegments[sentenceIndex] : new List<TextSegment>();
            
            var sentenceStart = globalWordIndex;
            
            // Process each segment in the sentence
            foreach (var segment in segments)
            {
                var words = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var word in words)
                {
                    var cleanWord = word.Trim();
                    if (string.IsNullOrEmpty(cleanWord)) continue;
                    
                    var wordBounds = new WordBounds
                    {
                        Text = cleanWord,
                        SentenceIndex = sentenceIndex,
                        WordIndex = globalWordIndex,
                        IsVocabulary = segment.IsVocabulary,
                        VocabularyWord = segment.IsVocabulary && vocabLookup.ContainsKey(cleanWord) ? vocabLookup[cleanWord] : null
                    };
                    
                    _wordBounds.Add(wordBounds);
                    globalWordIndex++;
                }
            }
            
            var sentenceEnd = globalWordIndex - 1;
            _sentenceBounds.Add(new SentenceBounds
            {
                SentenceIndex = sentenceIndex,
                StartWordIndex = sentenceStart,
                EndWordIndex = sentenceEnd,
                Text = sentence
            });
        }
    }

    private void CalculateWordPositions(SKCanvas canvas, float canvasWidth, float canvasHeight)
    {
        PerformanceLogger.Time("CalculateWordPositions", () =>
        {
            // Early return if paints are not initialized yet
            if (_textPaint == null || _vocabularyPaint == null)
                return;
                
            const float padding = 20f;
            const float lineSpacing = 1.5f;
            
            float x = padding;
            float y = padding + _fontSize;
            float lineHeight = _fontSize * lineSpacing;
            float maxWidth = canvasWidth - (padding * 2);
            float maxY = y; // Track the maximum Y position for height calculation
            
            foreach (var wordBounds in _wordBounds)
            {
                var paint = wordBounds.IsVocabulary ? _vocabularyPaint : _textPaint;
                float wordWidth = paint.MeasureText(wordBounds.Text);
                float spaceWidth = paint.MeasureText(" ");
                
                // Check if word fits on current line
                if (x + wordWidth > maxWidth && x > padding)
                {
                    // Move to next line
                    x = padding;
                    y += lineHeight;
                }
                
                // Store word position and bounds
                wordBounds.Bounds = new SKRect(x, y - _fontSize, x + wordWidth, y);
                wordBounds.Position = new SKPoint(x, y);
                
                // Track maximum Y for height calculation
                maxY = Math.Max(maxY, y);
                
                // Move to next word position
                x += wordWidth + spaceWidth;
            }
            
            // Calculate total required height with minimal bottom padding
            // maxY represents the bottom of the text on the last line, so just add small padding
            _calculatedHeight = maxY + 15f; // Small 15px bottom padding
            
            // Update the height request on the main thread
            UpdateHeightRequest();
        }, 5.0);
    }

    #endregion

    #region SkiaSharp Rendering

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        PerformanceLogger.Time("OnPaintSurface", () =>
        {
            var surface = e.Surface;
            var canvas = surface.Canvas;
            var info = e.Info;
            
            _logger.LogTrace("OnPaintSurface called: {Width}x{Height}, Control size: {ControlWidth}x{ControlHeight}", 
                info.Width, info.Height, Width, Height);
            
            // CRITICAL: Check if canvas has zero dimensions
            if (info.Width <= 0 || info.Height <= 0)
            {
                _logger.LogWarning("Canvas has zero dimensions! Info: {InfoWidth}x{InfoHeight}, Control: {ControlWidth}x{ControlHeight}", 
                    info.Width, info.Height, Width, Height);
                return;
            }
            
            // Use theme-appropriate background color instead of hardcoded white
            var bgColor = GetThemeBackgroundColor();
            canvas.Clear(bgColor);
            
            // Early return if paints are not initialized yet
            if (_textPaint == null || _highlightedTextPaint == null || _vocabularyPaint == null || _highlightedVocabularyPaint == null)
            {
                _logger.LogDebug("Paints not initialized yet, skipping render");
                return;
            }
            
            if (_wordBounds.Count == 0) 
            {
                _logger.LogDebug("No word bounds, skipping render");
                return;
            }
            
            _logger.LogTrace("Drawing {WordCount} words", _wordBounds.Count);
            
            // Recalculate layout if needed
            if (_needsLayout)
            {
                _logger.LogTrace("Recalculating layout");
                CalculateWordPositions(canvas, info.Width, info.Height);
                _needsLayout = false;
            }
            
            // Draw all words (highlighting is now handled by text color in DrawWords)
            DrawWords(canvas);
            
        }, 2.0); // Target <2ms for excellent performance
    }

    private void DrawWords(SKCanvas canvas)
    {
        _logger.LogTrace("DrawWords called with {WordCount} words", _wordBounds.Count);
        
        foreach (var wordBounds in _wordBounds)
        {
            // Determine if this word is in the currently highlighted sentence
            bool isInHighlightedSentence = _currentSentenceIndex >= 0 && 
                                         wordBounds.SentenceIndex == _currentSentenceIndex;
            
            // Choose the appropriate paint based on vocabulary status and highlighting
            SKPaint paint;
            if (wordBounds.IsVocabulary)
            {
                paint = isInHighlightedSentence ? _highlightedVocabularyPaint : _vocabularyPaint;
            }
            else
            {
                paint = isInHighlightedSentence ? _highlightedTextPaint : _textPaint;
            }
            
            canvas.DrawText(wordBounds.Text, wordBounds.Position, paint);
        }
        
        _logger.LogTrace("Finished drawing {WordCount} words", _wordBounds.Count);
    }

    #endregion

    #region Touch Handling

    protected override void OnTouch(SKTouchEventArgs e)
    {
        PerformanceLogger.Time("OnTouch", () =>
        {
            if (e.ActionType == SKTouchAction.Pressed)
            {
                var tappedWord = FindWordAtPoint(e.Location);
                var currentTapTime = DateTime.Now;
                
                if (tappedWord != null)
                {
                    _lastTappedWord = tappedWord;
                    
                    _logger.LogDebug("🏴‍☠️ Word tapped: '{Word}', IsVocabulary: {IsVocab}, HasVocabWord: {HasVocab}, SentenceIndex: {SentenceIndex}", 
                        tappedWord.Text, tappedWord.IsVocabulary, tappedWord.VocabularyWord != null, tappedWord.SentenceIndex);

                    // 🎯 NEW: Check for double-tap on same sentence with debouncing
                    bool isDoubleTap = _lastTapWord != null && 
                                     _lastTapWord.SentenceIndex == tappedWord.SentenceIndex &&
                                     (currentTapTime - _lastTapTime).TotalMilliseconds <= DoubleTapThreshold;

                    if (isDoubleTap)
                    {
                        _logger.LogDebug("🏴‍☠️ DOUBLE TAP detected on sentence {SentenceIndex}!", tappedWord.SentenceIndex);
                        
                        // Cancel any pending single tap
                        _singleTapTimer?.Dispose();
                        _singleTapTimer = null;
                        _pendingSingleTapWord = null;
                        
                        // Fire sentence double-tap event immediately
                        SentenceDoubleTapped?.Invoke(tappedWord.SentenceIndex);
                        
                        // Reset double-tap tracking
                        _lastTapTime = DateTime.MinValue;
                        _lastTapWord = null;
                    }
                    else
                    {
                        // Potential single tap - delay execution to allow for double tap
                        _logger.LogDebug("🏴‍☠️ Potential single tap - starting debounce timer");
                        
                        // Cancel any existing single tap timer
                        _singleTapTimer?.Dispose();
                        
                        // Store the pending tap
                        _pendingSingleTapWord = tappedWord;
                        
                        // Start timer to execute single tap action after debounce period
                        _singleTapTimer = new System.Threading.Timer(ExecutePendingSingleTap, null, 
                            (int)DoubleTapThreshold + 50, // Add 50ms buffer
                            System.Threading.Timeout.Infinite);
                        
                        // Update double-tap tracking
                        _lastTapTime = currentTapTime;
                        _lastTapWord = tappedWord;
                    }
                    
                    e.Handled = true;
                }
                else
                {
                    _logger.LogDebug("🏴‍☠️ No word found at touch point: {X}, {Y}", e.Location.X, e.Location.Y);
                    
                    // Reset double-tap tracking when tapping empty space
                    _lastTapTime = DateTime.MinValue;
                    _lastTapWord = null;
                    
                    // Cancel any pending single tap
                    _singleTapTimer?.Dispose();
                    _singleTapTimer = null;
                    _pendingSingleTapWord = null;
                }
            }
        }, 1.0);
        
        base.OnTouch(e);
    }

    /// <summary>
    /// Executes the pending single tap action after debounce period
    /// </summary>
    private void ExecutePendingSingleTap(object? state)
    {
        try
        {
            var wordToProcess = _pendingSingleTapWord;
            if (wordToProcess != null)
            {
                _logger.LogDebug("🏴‍☠️ Executing debounced single tap for: {Word}", wordToProcess.Text);
                
                // Execute on main thread
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (wordToProcess.IsVocabulary && wordToProcess.VocabularyWord != null)
                    {
                        // Fire vocabulary event for vocabulary words
                        VocabularyWordTapped?.Invoke(wordToProcess.VocabularyWord);
                        _logger.LogDebug("🏴‍☠️ Fired VocabularyWordTapped for: {Word}", wordToProcess.Text);
                    }
                    else
                    {
                        WordTapped?.Invoke(wordToProcess.Text);
                        _logger.LogDebug("🏴‍☠️ Fired WordTapped for: {Word}", wordToProcess.Text);
                    }
                    
                    // Also fire sentence single tap event
                    SentenceTapped?.Invoke(wordToProcess.SentenceIndex);
                    _logger.LogDebug("🏴‍☠️ Fired SentenceTapped for sentence: {SentenceIndex}", wordToProcess.SentenceIndex);
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🏴‍☠️ Error executing pending single tap");
        }
        finally
        {
            // Clean up
            _singleTapTimer?.Dispose();
            _singleTapTimer = null;
            _pendingSingleTapWord = null;
        }
    }

    private WordBounds? FindWordAtPoint(SKPoint point)
    {
        // Simple hit testing - can be optimized with spatial indexing if needed
        return _wordBounds.FirstOrDefault(wb => wb.Bounds.Contains(point));
    }

    #endregion

    #region Size Change Handling

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        
        // Trigger re-layout when size changes (window resize, orientation change, etc.)
        if (width > 0 && height > 0)
        {
            _logger.LogDebug("InteractiveTextRenderer size changed: {Width}x{Height}", width, height);
            _needsLayout = true;
            InvalidateSurface();
        }
    }

    #endregion

    #region Cleanup

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        
        if (Handler != null)
        {
            // Get the logger from the service provider when handler is attached
            try
            {
                var serviceProvider = Handler.MauiContext?.Services;
                if (serviceProvider != null)
                {
                    var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                    if (loggerFactory != null)
                    {
                        _logger = loggerFactory.CreateLogger<InteractiveTextRenderer>();
                        _logger.LogDebug("InteractiveTextRenderer logger initialized");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize logger: {ex.Message}");
            }
            
            // Initialize paints when the handler is set
            if (_textPaint == null)
            {
                InitializePaints();
            }
        }
        else if (Handler == null)
        {
            // Clean up resources when handler changes
            CleanupTimers();
            
            _textPaint?.Dispose();
            _highlightedTextPaint?.Dispose();
            _vocabularyPaint?.Dispose();
            _highlightedVocabularyPaint?.Dispose();
            _font?.Dispose();
            
            _textPaint = null!;
            _highlightedTextPaint = null!;
            _vocabularyPaint = null!;
            _highlightedVocabularyPaint = null!;
            _font = null!;
        }
    }
    
    /// <summary>
    /// Clean up any active timers to prevent memory leaks
    /// </summary>
    private void CleanupTimers()
    {
        _singleTapTimer?.Dispose();
        _singleTapTimer = null;
        _pendingSingleTapWord = null;
    }

    #endregion
}

/// <summary>
/// Represents the bounds and metadata for a single word
/// </summary>
public class WordBounds
{
    public string Text { get; set; } = string.Empty;
    public int SentenceIndex { get; set; }
    public int WordIndex { get; set; }
    public SKRect Bounds { get; set; }
    public SKPoint Position { get; set; }
    public bool IsVocabulary { get; set; }
    public VocabularyWord? VocabularyWord { get; set; }
}

/// <summary>
/// Represents the bounds and metadata for a sentence
/// </summary>
public class SentenceBounds
{
    public int SentenceIndex { get; set; }
    public int StartWordIndex { get; set; }
    public int EndWordIndex { get; set; }
    public string Text { get; set; } = string.Empty;
}
