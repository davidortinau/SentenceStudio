using CommunityToolkit.Maui.Views;

namespace SentenceStudio.Pages.Scene;

public partial class ImageGalleryPopup : Popup
{
	public ImageGalleryPopup(DescribeAScenePageModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
	}

	private async void OnCloseClicked(object sender, EventArgs e)
	{
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    	await CloseAsync(false, cts.Token);
	}
}