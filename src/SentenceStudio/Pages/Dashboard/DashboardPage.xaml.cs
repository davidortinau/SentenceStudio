namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPage : ContentPage
{
	public DashboardPage(DashboardPageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}

	
}

