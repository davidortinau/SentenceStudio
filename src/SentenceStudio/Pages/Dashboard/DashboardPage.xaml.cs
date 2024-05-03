
using Sharpnado.Tasks;

namespace SentenceStudio.Pages.Dashboard;

public partial class DashboardPage : ContentPage
{
    private DashboardPageModel _model;

    public DashboardPage(DashboardPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		
		
	}

}

