using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SentenceStudio.Pages.Vocabulary;

public partial class EditVocabularyPage : ContentPage
{
    public EditVocabularyPage(EditVocabularyPageModel pageModel)
    {
        InitializeComponent();

        BindingContext = pageModel;
    }
}