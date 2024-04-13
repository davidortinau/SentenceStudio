using System.Collections.Specialized;
using System.ComponentModel;

namespace SentenceStudio.Pages.Lesson;

public partial class WarmupPage : ContentPage
{
    WarmupPageModel _model;

    public WarmupPage(WarmupPageModel model)
    {
        InitializeComponent();

        BindingContext = _model = model;
        _model.Chunks.CollectionChanged += ChunksCollectionChanged;
    }

    private void ConversationViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WarmupPageModel.Chunks))
        {
            if (_model.Chunks != null)
            {
                _model.Chunks.CollectionChanged += ChunksCollectionChanged;
            }
        }
    }

    private void ChunksCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            this.Dispatcher.DispatchAsync(async () =>
            {
                await Task.Delay(100); // Wait for the UI to finish updating
                await MessageCollectionView.ScrollToAsync(0, MessageCollectionView.ContentSize.Height, animated: true);
            });
        }
    }
}