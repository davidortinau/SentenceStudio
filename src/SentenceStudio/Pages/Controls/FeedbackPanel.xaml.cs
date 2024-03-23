namespace SentenceStudio.Pages.Controls;

public partial class FeedbackPanel : Border
{
	public static readonly BindableProperty FeedbackProperty = BindableProperty.Create(
		propertyName: "Feedback",
		returnType: typeof(string),
		declaringType: typeof(FeedbackPanel),
		defaultValue: string.Empty);

	public string Feedback
	{
		get { return (string)GetValue(FeedbackProperty); }
		set { SetValue(FeedbackProperty, value); }
	}

	private static void FeedbackPropertyChanged(BindableObject bindable, object oldValue, object newValue)
{
    var control = (FeedbackPanel)bindable;
    if (control.BindingContext is FeedbackPanelModel viewModel)
    {
        viewModel.Feedback = (string)newValue;
    }
}
	public FeedbackPanel()
	{
		InitializeComponent();
		// BindingContext = ServiceProvider.GetService<FeedbackPanelModel>();
	}
}