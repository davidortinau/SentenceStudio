namespace SentenceStudio.Pages.Scene;

public partial class DescribeAScenePage : ContentPage
{

	public DescribeAScenePage(DescribeAScenePageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}
}