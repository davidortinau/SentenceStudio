namespace SentenceStudio.Pages.Controls;


public partial class ModeSelector : Border
{
	//[ObservableProperty]
	private string _selectedMode;
	public string SelectedMode
	{
		get
		{
			return _selectedMode;
		}
		set
		{
			if (_selectedMode != value)
			{
				_selectedMode = value;
				OnPropertyChanged(nameof(SelectedMode));
			}
			
		}
	}

	public ModeSelector()
	{
		InitializeComponent();

		BindingContext = this;
	}
}

