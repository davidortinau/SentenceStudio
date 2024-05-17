using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Lesson;

public partial class WritingPage : ContentPage
{
	WritingPageModel _model;

	public WritingPage(WritingPageModel model)
	{
		InitializeComponent();
		BindingContext = _model = model;
	}
}