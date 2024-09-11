using CommunityToolkit.Maui.Converters;
using CommunityToolkit.Maui.Markup;
using CustomLayouts;
using Microsoft.Maui.Controls.Shapes;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Skills;

public class ListSkillProfilesPage : ContentPage
{
	private ListSkillProfilesPageModel _model;
	public ListSkillProfilesPage(ListSkillProfilesPageModel model)
	{
		BindingContext = _model = model;
		Build();	
	}

	public void Build()
	{
		Title = "Skill Profiles";

		Content = new ScrollView { 
			Content = new VerticalStackLayout
			{
				Spacing = (double)Application.Current.Resources["size240"],
				Children = {
					new HorizontalWrapLayout
					{
						Spacing = (double)Application.Current.Resources["size320"]	
					}
					.ItemTemplate(() =>
						new Border
						{
							StrokeShape = new Rectangle(),
							StrokeThickness = 1,
							Content = new Grid
								{
									Children = {
										new Label {  }.Bind(Label.TextProperty, nameof(SkillProfile.Title)).Center()
									}
								}
								.Size(300,120)	
								.BindTapGesture(
									commandPath: nameof(ListSkillProfilesPageModel.EditProfileCommand),
									commandSource: _model,
									parameterPath: ".") // Grid
						} // Border
					)
					.Bind(BindableLayout.ItemsSourceProperty, nameof(ListSkillProfilesPageModel.Profiles)),
					AddButton()
							.BindTapGesture(
									commandPath: nameof(ListSkillProfilesPageModel.AddProfileCommand),
									commandSource: _model)
						.Start()

				}
			}
		}.Margin((double)Application.Current.Resources["size160"]);
	}

    private Border AddButton()
    {
        return 
			new Border{
				StrokeShape = new Rectangle(),
				StrokeThickness = 1,
					Content = new Grid
					{
						Children = {
							new Label { Text = "Add" }.Center()
						}
					}.Size(300,120)
			};
    }
}