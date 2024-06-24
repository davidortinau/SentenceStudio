using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Clozure;

public partial class ClozurePage : ContentPage
{
	ClozurePageModel _model;

	public ClozurePage(ClozurePageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		ModeSelector.PropertyChanged += Mode_PropertyChanged;

		// VisualStateManager.GoToState(InputUI, );
	}

    private void Mode_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "SelectedMode")
		{
			// Do something when SelectedMode changes
			VisualStateManager.GoToState(InputUI, ModeSelector.SelectedMode);
		}
	}

	

}