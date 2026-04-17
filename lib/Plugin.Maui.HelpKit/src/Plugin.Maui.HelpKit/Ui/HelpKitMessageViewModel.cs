using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Plugin.Maui.HelpKit.Ui;

/// <summary>
/// Presentation wrapper around a <see cref="HelpKitMessage"/> so the chat
/// view can mutate <see cref="Content"/> while a response streams in.
/// </summary>
internal sealed class HelpKitMessageViewModel : INotifyPropertyChanged
{
    public enum Kind
    {
        User,
        Assistant,
        Error,
    }

    private string _content;
    private IReadOnlyList<HelpKitCitation> _citations;

    public HelpKitMessageViewModel(Kind kind, string content, IReadOnlyList<HelpKitCitation>? citations = null)
    {
        MessageKind = kind;
        _content = content ?? string.Empty;
        _citations = citations ?? Array.Empty<HelpKitCitation>();
        Timestamp = DateTimeOffset.Now;
    }

    public Kind MessageKind { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsUser => MessageKind == Kind.User;
    public bool IsAssistant => MessageKind == Kind.Assistant;
    public bool IsError => MessageKind == Kind.Error;

    public string Role => MessageKind switch
    {
        Kind.User => "user",
        Kind.Assistant => "assistant",
        Kind.Error => "error",
        _ => "unknown",
    };

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccessibilityName));
            OnPropertyChanged(nameof(HasCitations));
        }
    }

    public IReadOnlyList<HelpKitCitation> Citations
    {
        get => _citations;
        set
        {
            _citations = value ?? Array.Empty<HelpKitCitation>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCitations));
            OnPropertyChanged(nameof(CitationsDisplay));
        }
    }

    public bool HasCitations => Citations.Count > 0;

    public ObservableCollection<HelpKitCitation> CitationsDisplay
        => new(Citations);

    /// <summary>
    /// Flattened text used by platform screen readers. Reads "{Role}: {short excerpt}".
    /// </summary>
    public string AccessibilityName
    {
        get
        {
            var excerpt = Content.Length > 120 ? Content[..120] + "..." : Content;
            return $"{Role}: {excerpt}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
