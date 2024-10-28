using CommunityToolkit.Maui.Markup;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Skills;

public class EditSkillProfilePage : ContentPage
{
	public EditSkillProfilePage(EditSkillProfilePageModel model)
	{
		BindingContext = model;
		Build();	
	}

	public void Build()
	{
		Title = "Edit Skill Profile";

		ToolbarItem saveButton = new ToolbarItem
		{
			Text = "Save"
		}.BindCommand(nameof(EditSkillProfilePageModel.SaveCommand));

		ToolbarItems.Add(saveButton);

		var deleteButton = new ToolbarItem
		{
			Text = "Delete"
		}.BindCommand(nameof(EditSkillProfilePageModel.DeleteCommand));

		ToolbarItems.Add(deleteButton);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout{
				Children = {
					new FormField{ 
						FieldLabel ="Title", 
						Content = new Entry().Bind(Entry.TextProperty, nameof(EditSkillProfilePageModel.Title))
					},
					new FormField{ 
						FieldLabel ="Skills Description", 
						Content = new Editor{ 
								AutoSize = EditorAutoSizeOption.TextChanges 
							}
							.Bind(Editor.TextProperty, nameof(EditSkillProfilePageModel.Description))
					},
					new Label()
						.Bind(Label.TextProperty, nameof(EditSkillProfilePageModel.Profile.CreatedAt), stringFormat: "Created: {0:MM/dd/yyyy}"),
					new Label()
						.Bind(Label.TextProperty, nameof(EditSkillProfilePageModel.Profile.UpdatedAt), stringFormat: "Updated: {0:MM/dd/yyyy}"),
				}
			}
			
		}.Margins((double)Application.Current.Resources["size160"]);
	}
}