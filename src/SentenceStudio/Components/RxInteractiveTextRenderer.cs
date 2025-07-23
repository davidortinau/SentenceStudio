using MauiReactor;
using SentenceStudio.Components;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Components;

/// <summary>
/// MauiReactor wrapper for the InteractiveTextRenderer SkiaSharp component.
/// Provides high-performance text rendering with word-level interaction.
/// </summary>
public class RxInteractiveTextRenderer : VisualNode<InteractiveTextRenderer>
{
    // Content properties
    private List<string>? _sentences;
    private List<VocabularyWord>? _vocabularyWords;
    private List<List<TextSegment>>? _sentenceSegments;
    private int _currentSentenceIndex = -1;
    private float _fontSize = 18f;
    private double _heightRequest = -1;
    private LayoutOptions _horizontalOptions = LayoutOptions.Fill;
    
    // Events
    private Action<string>? _wordTapped;
    private Action<VocabularyWord>? _vocabularyWordTapped;
    private Action<List<(string, int)>>? _paragraphTapped;
    private Action<List<(string, int)>>? _paragraphDoubleTapped;

    public RxInteractiveTextRenderer()
    {
    }

    public RxInteractiveTextRenderer(
        List<string> sentences,
        List<VocabularyWord> vocabularyWords,
        List<List<TextSegment>> sentenceSegments)
    {
        _sentences = sentences;
        _vocabularyWords = vocabularyWords;
        _sentenceSegments = sentenceSegments;
    }

    #region Fluent API

    /// <summary>
    /// Sets the content for the text renderer
    /// </summary>
    public RxInteractiveTextRenderer Content(
        List<string> sentences,
        List<VocabularyWord> vocabularyWords,
        List<List<TextSegment>> sentenceSegments)
    {
        _sentences = sentences;
        _vocabularyWords = vocabularyWords;
        _sentenceSegments = sentenceSegments;
        return this;
    }

    /// <summary>
    /// Sets which sentence is currently highlighted
    /// </summary>
    public RxInteractiveTextRenderer CurrentSentence(int sentenceIndex)
    {
        _currentSentenceIndex = sentenceIndex;
        return this;
    }

    /// <summary>
    /// Sets the font size for text rendering
    /// </summary>
    public RxInteractiveTextRenderer FontSize(float fontSize)
    {
        System.Diagnostics.Debug.WriteLine($"🔤 RxInteractiveTextRenderer.FontSize called: {fontSize}");
        _fontSize = fontSize;
        return this;
    }

    /// <summary>
    /// Sets the height request
    /// </summary>
    public RxInteractiveTextRenderer HeightRequest(double height)
    {
        _heightRequest = height;
        return this;
    }

    /// <summary>
    /// Sets the horizontal layout options
    /// </summary>
    public RxInteractiveTextRenderer HorizontalOptions(LayoutOptions options)
    {
        _horizontalOptions = options;
        return this;
    }

    /// <summary>
    /// Event fired when a word is tapped
    /// </summary>
    public RxInteractiveTextRenderer OnWordTapped(Action<string> callback)
    {
        _wordTapped = callback;
        return this;
    }

    /// <summary>
    /// Event fired when a vocabulary word is tapped
    /// </summary>
    public RxInteractiveTextRenderer OnVocabularyWordTapped(Action<VocabularyWord> callback)
    {
        _vocabularyWordTapped = callback;
        return this;
    }

    /// <summary>
    /// Event fired when a paragraph is tapped
    /// </summary>
    public RxInteractiveTextRenderer OnParagraphTapped(Action<List<(string, int)>> callback)
    {
        _paragraphTapped = callback;
        return this;
    }

    /// <summary>
    /// Event fired when a paragraph is double-tapped
    /// </summary>
    public RxInteractiveTextRenderer OnParagraphDoubleTapped(Action<List<(string, int)>> callback)
    {
        _paragraphDoubleTapped = callback;
        return this;
    }

    #endregion

    #region VisualNode Implementation

    protected override void OnMount()
    {
        base.OnMount();
        
        if (NativeControl == null)
        {
            System.Diagnostics.Debug.WriteLine("❌ RxInteractiveTextRenderer: NativeControl is null after mount!");
            return;
        }

        // Wire up events
        NativeControl.WordTapped += OnWordTappedInternal;
        NativeControl.VocabularyWordTapped += OnVocabularyWordTappedInternal;
        NativeControl.ParagraphTapped += OnParagraphTappedInternal;
        NativeControl.ParagraphDoubleTapped += OnParagraphDoubleTappedInternal;
    }

    protected override void OnUnmount()
    {
        if (NativeControl != null)
        {
            // Unwire events
            NativeControl.WordTapped -= OnWordTappedInternal;
            NativeControl.VocabularyWordTapped -= OnVocabularyWordTappedInternal;
            NativeControl.ParagraphTapped -= OnParagraphTappedInternal;
            NativeControl.ParagraphDoubleTapped -= OnParagraphDoubleTappedInternal;
        }

        base.OnUnmount();
    }

    protected override void OnUpdate()
    {
        if (NativeControl == null)
        {
            System.Diagnostics.Debug.WriteLine("❌ RxInteractiveTextRenderer: NativeControl is null in OnUpdate!");
            return;
        }

        // Update content if provided
        if (_sentences != null && _vocabularyWords != null && _sentenceSegments != null)
        {
            NativeControl.SetContent(_sentences, _vocabularyWords, _sentenceSegments);
        }

        // Update current sentence highlighting
        NativeControl.SetCurrentSentence(_currentSentenceIndex);

        // Update font size
        NativeControl.SetFontSize(_fontSize);

        // Update layout properties
        if (_heightRequest > 0)
        {
            NativeControl.HeightRequest = _heightRequest;
        }
        
        NativeControl.HorizontalOptions = _horizontalOptions;

        base.OnUpdate();
    }

    #endregion

    #region Event Handlers

    private void OnWordTappedInternal(string word)
    {
        _wordTapped?.Invoke(word);
    }

    private void OnVocabularyWordTappedInternal(VocabularyWord vocabularyWord)
    {
        _vocabularyWordTapped?.Invoke(vocabularyWord);
    }

    private void OnParagraphTappedInternal(List<(string, int)> paragraph)
    {
        _paragraphTapped?.Invoke(paragraph);
    }

    private void OnParagraphDoubleTappedInternal(List<(string, int)> paragraph)
    {
        _paragraphDoubleTapped?.Invoke(paragraph);
    }

    #endregion
}
