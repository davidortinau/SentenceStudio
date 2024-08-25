using Plugin.Maui.Audio;
using CommunityToolkit.Maui.Converters;
using SentenceStudio.Converters;
using Shiny;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SentenceStudio.Pages.Controls;

public class AudioControls : Border, INotifyPropertyChanged
{
    public static readonly BindableProperty StreamProperty = BindableProperty.Create(
        nameof(Stream),
        typeof(Stream),
        typeof(AudioControls),
        null,
        propertyChanged: OnStreamChanged);

    public Stream Stream
    {
        get => (Stream)GetValue(StreamProperty);
        set => SetValue(StreamProperty, value);
    }

    private static void OnStreamChanged(BindableObject bindable, object oldValue, object newValue)
    {
        // Handle the stream change logic here
        Debug.WriteLine("Ready to play audio");
    }

    public bool IsPlaying => _player?.IsPlaying ?? false;
    
    private IAudioPlayer _player;
    private bool _isSeeking;
    private Slider _slider;

    public AudioControls()
    {
        Build();
        // this.IsEnabled = false;
    }

    public void Build()
    {
        Background = (Color)Application.Current.Resources["DarkBackground"];
        StrokeShape = new RoundRectangle { CornerRadius = 12 };
        Stroke = (Color)Application.Current.Resources["DarkBackground"];
        StrokeThickness = 2;
        Padding = new Thickness((Double)Application.Current.Resources["size120"]);

        Content = new Grid
        {
            RowDefinitions = Rows.Define(Auto,20),
            ColumnDefinitions = Columns.Define(Star,Star,Star),
            RowSpacing = (Double)Application.Current.Resources["size120"],
            ColumnSpacing = (Double)Application.Current.Resources["size120"],
            Children = {
                new Button {  }
                    .Icon(SegoeFluentIcons.Pause)
                    .IconSize(24)
                    .IconColor((Color)Application.Current.Resources["LightOnDarkBackground"])
                    .Background(Colors.Transparent)
                    .Column(1)
                    .OnClicked(Pause)
                    .Bind(Button.IsVisibleProperty, nameof(IsPlaying)),
                new Button {  }
                    .Icon(SegoeFluentIcons.Play)
                    .IconSize(24)
                    .IconColor((Color)Application.Current.Resources["LightOnDarkBackground"])
                    .Background(Colors.Transparent)
                    .Column(1)
                    .OnClicked(Play)
                    .Bind(Button.IsVisibleProperty, nameof(IsPlaying), converter: new InvertedBoolConverter()),
                new Label {  }
                    .Text("00:00")
                    .TextColor((Color)Application.Current.Resources["LightOnDarkBackground"])
                    .Column(0)
                    .Bind(Label.TextProperty, nameof(_player.CurrentPosition), converter: new SecondsToStringConverter()),
                new Label {  }
                    .Text("00:00")
                    .TextColor((Color)Application.Current.Resources["LightOnDarkBackground"])
                    .Column(2)
                    .Bind(Label.TextProperty, nameof(_player.Duration), converter: new SecondsToStringConverter()),
                new Slider{}
                    .Row(1)
                    .ColumnSpan(3)
                    .Assign(out _slider)
                    .OnValueChanged(OnSliderValueChanged)
            }
        };
    }

    private void Play(object sender, EventArgs e)
    {
        if(_player is null && Stream is not null)
        {
            _player = AudioManager.Current.CreatePlayer(Stream);
        }
        
        _player.PlaybackEnded += OnPlaybackEnded;
        _player?.Play();
        Device.StartTimer(TimeSpan.FromMilliseconds(500), UpdateSlider);
        NotifyPropertyChanged(nameof(IsPlaying));
    }

    private void Pause(object sender, EventArgs e)
    {
        _player?.Pause();
        NotifyPropertyChanged(nameof(IsPlaying));
        
    }

    private void OnSliderValueChanged(object sender, ValueChangedEventArgs e)
    {
        if (_isSeeking)
        {
            return;
        }

        if (_player != null && _player.Duration > 0)
        {
            var position = TimeSpan.FromSeconds(_player.Duration * (e.NewValue / 100));
            _player.Seek(position.Milliseconds);
        }
    }

    private bool UpdateSlider()
    {
        if (_player != null && _player.Duration > 0 && !_isSeeking)
        {
            _isSeeking = true;
            _slider.Value = (_player.CurrentPosition / _player.Duration) * 100;
            _isSeeking = false;
        }

        return _player?.IsPlaying ?? false;
    }

    private void OnPlaybackEnded(object sender, EventArgs e)
    {
        _slider.Value = 0;
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}