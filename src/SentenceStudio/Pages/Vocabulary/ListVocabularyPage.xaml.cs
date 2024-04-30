namespace SentenceStudio.Pages.Vocabulary;

public partial class ListVocabularyPage : ContentPage
{
	public ListVocabularyPage(ListVocabularyPageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}
}