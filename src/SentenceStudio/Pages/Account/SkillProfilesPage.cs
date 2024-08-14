using CommunityToolkit.Maui.Markup;
using SentenceStudio.Pages.Controls;

namespace SentenceStudio.Pages.Account;

public class SkillProfilesPage : ContentPage
{
	public SkillProfilesPage(SkillProfilesPageModel model)
	{
		BindingContext = model;
		Build();	
	}

	public void Build()
	{
		Title = "Skill Profiles";

		var addButton = new ToolbarItem
		{
			Text = "Add"
		}.BindCommand(nameof(SkillProfilesPageModel.AddProfileCommand));

		ToolbarItems.Add(addButton);

		var saveButton = new ToolbarItem
		{
			Text = "Save"
		}.BindCommand(nameof(SkillProfilesPageModel.SaveProfilesCommand));

		ToolbarItems.Add(saveButton);

		Content = new Grid
		{
			Children = {
				new CollectionView
				{
					ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical)
					{
						ItemSpacing = 10
					},
					ItemSizingStrategy = ItemSizingStrategy.MeasureAllItems,
					ItemTemplate = new DataTemplate(() =>
					{
						var titleEntry = new Entry();
						titleEntry.SetBinding(Entry.TextProperty, "Title");

						var descriptionEditor = new Editor();
						descriptionEditor.SetBinding(Editor.TextProperty, "Description");

						return new VerticalStackLayout
						{
							Spacing = 10,
							Children = { 
								new Label {  }.Bind(Label.TextProperty, nameof(SkillProfile.ID)),
								new FormField{ FieldLabel = "Title", Content = titleEntry },
								new FormField{ FieldLabel = "Description", Content = descriptionEditor },
								new BoxView { HeightRequest = 1 }
									.FillHorizontal()
									.AppThemeColorBinding(BoxView.ColorProperty, light: (Color)Application.Current.Resources["DarkOnLightBackground"], dark: (Color)Application.Current.Resources["LightOnDarkBackground"])
							}
						};
					}),
					EmptyView = new Label { Text = "No profiles found" }
				}.Bind(CollectionView.ItemsSourceProperty, nameof(SkillProfilesPageModel.Profiles))
			}
		}.Margins((double)Application.Current.Resources["size160"]);
	}
}