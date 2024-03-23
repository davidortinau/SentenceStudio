using SentenceStudio;

namespace SentenceStudio.Pages.Vocabulary;

public partial class AddVocabularyPage : ContentPage
{
	public AddVocabularyPage(AddVocabularyPageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}
}