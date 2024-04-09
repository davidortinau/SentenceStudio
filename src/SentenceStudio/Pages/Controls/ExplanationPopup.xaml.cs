using CommunityToolkit.Maui.Views;

namespace SentenceStudio.Pages.Controls;

public partial class ExplanationPopup : Popup
{
	public ExplanationPopup()
	{
		InitializeComponent();
	}

	private async void OnCloseClicked(object sender, EventArgs e)
	{
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    	await CloseAsync(false, cts.Token);
	}
   
}