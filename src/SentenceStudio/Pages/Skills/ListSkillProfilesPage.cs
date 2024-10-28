using CommunityToolkit.Maui.Behaviors;
using CustomLayouts;

namespace SentenceStudio.Pages.Skills;

public class ListSkillProfilesPage : ContentPage
{
	private ListSkillProfilesPageModel _model;
	public ListSkillProfilesPage(ListSkillProfilesPageModel model)
	{
		BindingContext = _model = model;
		InitBehaviors();
		Build();	
	}

    private void InitBehaviors()
    {
        this.Behaviors.Add(
			new EventToCommandBehavior
			{
				EventName = nameof(ContentPage.NavigatedTo),
				Command = _model.NavigatedToCommand
			}
		);

		this.Behaviors.Add(
			new EventToCommandBehavior
			{
				EventName = nameof(ContentPage.NavigatedFrom),
				Command = _model.NavigatedFromCommand
			}
		);

		this.Behaviors.Add(
			new EventToCommandBehavior
			{
				EventName = nameof(ContentPage.Appearing),
				Command = _model.AppearingCommand
			}
		);
    }

    public void Build()
	{
		Title = "Skill Profiles";
		// Shell.SetTabBarIsVisible(this, false); // Hide the tab bar - no clue why it's appearing here

		

		Content = new ScrollView { 
			Content = new VerticalStackLayout
			{
				Padding = (double)Application.Current.Resources["size160"],
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
		};
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