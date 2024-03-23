namespace SentenceStudio.Pages.Lesson;

public partial class LessonStartPage : ContentPage
{
	public LessonStartPage(LessonStartPageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}


}