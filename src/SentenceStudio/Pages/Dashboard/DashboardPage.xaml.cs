namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPage : ContentPage
{
	public DashboardPage(DashboardPageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}

	protected override void OnNavigatedTo(NavigatedToEventArgs args)
	{
		base.OnNavigatedTo(args);

		(BindingContext as DashboardPageModel).Init();
	}
}

