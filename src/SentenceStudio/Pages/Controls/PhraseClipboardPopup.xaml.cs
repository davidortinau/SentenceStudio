using CommunityToolkit.Maui.Views;

namespace SentenceStudio.Pages.Controls;

public partial class PhraseClipboardPopup : Popup
{
	public PhraseClipboardPopup(PhraseClipboardViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
	}

	private async void OnCloseClicked(object sender, EventArgs e)
	{
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    	await CloseAsync(false, cts.Token);
	}

	private async void OnItemTapped(object sender, EventArgs e)
	{
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    	await CloseAsync((sender as Label).Text, cts.Token);
	}
 
   
}