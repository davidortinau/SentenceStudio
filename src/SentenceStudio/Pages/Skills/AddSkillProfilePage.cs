using CommunityToolkit.Maui.Markup;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Skills;

public class AddSkillProfilePage : ContentPage
{
	public AddSkillProfilePage(AddSkillProfilePageModel model)
	{
		BindingContext = model;
		Build();	
	}

	public void Build()
	{
		Title = "Add Skill Profile";

		ToolbarItem saveButton = new ToolbarItem
		{
			Text = "Save"
		}.BindCommand(nameof(AddSkillProfilePageModel.SaveCommand));

		ToolbarItems.Add(saveButton);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout{
				Children = {
					new FormField{ 
						FieldLabel ="Title", 
						Content = new Entry().Bind(Entry.TextProperty, nameof(AddSkillProfilePageModel.Title))
					},
					new FormField{ 
						FieldLabel ="Skills Description", 
						Content = new Editor{ 
								AutoSize = EditorAutoSizeOption.TextChanges 
							}
							.Bind(Editor.TextProperty, nameof(AddSkillProfilePageModel.Description))
					},
				}
			}.Margins((double)Application.Current.Resources["size160"])
			
		};
	}
}