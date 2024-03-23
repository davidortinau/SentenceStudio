using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Lesson;

public partial class WarmupPage : ContentPage
{
	WarmupPageModel _model;

	public WarmupPage(WarmupPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		_model.PropertyChanged += ConversationViewModelPropertyChanged;
            // _model.InitAsync();
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
                foreach (ConversationChunk item in e.NewItems)
                {
                    if (item.Author == ConversationParticipant.Me)
                    {
                        MessageCollectionView.ScrollTo(0);
                    }
                }
            }
        }

}