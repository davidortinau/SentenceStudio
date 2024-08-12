namespace SentenceStudio.Pages.Controls;

public class DesktopTitleBar : TitleBar
{
	public DesktopTitleBar()
	{
		Build();
	}

	public void Build()
	{
		Content = new VerticalStackLayout
		{
			Children = {
				new Label { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Text = "Welcome to .NET MAUI!"
				}
			}
		};

		TrailingContent = new Grid(){

		};
	}
}