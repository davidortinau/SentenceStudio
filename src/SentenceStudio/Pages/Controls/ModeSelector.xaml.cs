namespace SentenceStudio.Pages.Controls;


public partial class ModeSelector : Border
{


	// Define the BindableProperty
    public static readonly BindableProperty SelectedModeProperty = BindableProperty.Create(
        propertyName: nameof(SelectedMode),
        returnType: typeof(string),
        declaringType: typeof(ModeSelector),
        defaultValue: string.Empty,
        defaultBindingMode: BindingMode.TwoWay, // Allows for two-way binding by default
        propertyChanged: OnSelectedModeChanged); // Optional: Respond to changes

    // Property wrapper
    public string SelectedMode
    {
        get => (string)GetValue(SelectedModeProperty);
        set => SetValue(SelectedModeProperty, value);
    }

    public ModeSelector()
    {
        InitializeComponent();
    }

    // Optional: A method to handle changes to the SelectedMode property
    private static void OnSelectedModeChanged(BindableObject bindable, object oldValue, object newValue)
    {
        // Handle the property change as needed
		Debug.WriteLine($"SelectedMode changed from {oldValue} to {newValue}");	
    }
}

