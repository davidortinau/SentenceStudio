using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.SyntacticAnalysis;

public partial class AnalysisPage : ContentPage
{
	AnalysisPageModel _model;

	public AnalysisPage(AnalysisPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		
	}

}