using CommunityToolkit.Maui.Markup;
using Switch = Microsoft.Maui.Controls.Switch;

namespace SentenceStudio.Pages.Controls;

public class DesktopTitleBar : ContentView
{

	public DesktopTitleBar()
	{
		BindingContext = ServiceProvider.GetService<DesktopTitleBarViewModel>();	
		Build();
	}

	public DesktopTitleBar(DesktopTitleBarViewModel viewModel)
	{
		BindingContext = viewModel;
		Build();
	}

	public void Build()
	{
		Content = new VerticalStackLayout
		{
			Children = {
				new Label { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Text = "Sentence Studio"
				}
			}
		};

		// TrailingContent = new HorizontalStackLayout(){
		// 	Spacing = 10,
		// 	Children = {
		// 		new Button { }
		// 			.Bind(Button.TextProperty, nameof(DesktopTitleBarViewModel.SelectedLanguage))
		// 			.CenterVertical(),
		// 		new Button { }
		// 			.Bind(Button.TextProperty, nameof(DesktopTitleBarViewModel.SelectedProfileTitle))
		// 			.CenterVertical(),
		// 		new Switch {}  
		// 			.CenterVertical()
		// 	}
		// };
	}
}