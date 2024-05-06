using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using SentenceStudio.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Pages.Translation;

public partial class TranslationPage : ContentPage
{
	TranslationPageModel _model;

	public TranslationPage(TranslationPageModel model)
	{
		InitializeComponent();

		BindingContext = _model = model;
		// model.PropertyChanged += Model_PropertyChanged;
		ModeSelector.PropertyChanged += Mode_PropertyChanged;

		VisualStateManager.GoToState(InputUI, PlayMode.Keyboard.ToString());
	}

    private async void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
		

		if (e.PropertyName == "IsBusy" || (_model.IsBusy == true && LoadingOverlay.Children.Count() < 4))
		{

            await Task.Delay(200);
            
            if (_model.IsBusy)
			{
				// Remove all labels except the first one
				if (LoadingOverlay.Children.Count > 1)
				{
					for (int i = LoadingOverlay.Children.Count - 1; i > 0; i--)
					{
						if (LoadingOverlay.Children[i] is Label)
						{
							LoadingOverlay.Children.RemoveAt(i);
						}
					}
				}
				
				
				foreach (var term in _model.Terms)
				{
					await PlaceLabel(term.TargetLanguageTerm, Colors.White);
					await PlaceLabel(term.NativeLanguageTerm, Colors.Orange);
					await Task.Delay(200);
				}
			}
		}
    }

	private async Task PlaceLabel(string term, Color typeColor)
	{
		var textField = new Label { Text = term, TextColor = typeColor };
		textField.Opacity = 0;

		// Generate random position and font size
		Random random = new Random();
		double x = random.NextDouble() * (LoadingOverlay.Width - textField.Width);
		double y = random.NextDouble() * (LoadingOverlay.Height - textField.Height);
		double fontSize = random.Next(24, 82); // Change this range as needed

		textField.FontSize = fontSize;

		// Check for collisions with existing labels
		bool collisionDetected = true;
		while (collisionDetected)
		{
			collisionDetected = false;

			// Iterate through existing labels
			foreach (var child in LoadingOverlay.Children)
			{
				if (child is Label existingLabel)
				{
					// Get the bounds of the existing label
					var existingBounds = AbsoluteLayout.GetLayoutBounds(existingLabel);

					// Check for collision with existing label
					if (x < existingBounds.Right && x + textField.Width > existingBounds.Left &&
						y < existingBounds.Bottom && y + textField.Height > existingBounds.Top)
					{
						// Adjust the position of the label
						x = random.NextDouble() * (LoadingOverlay.Width - textField.Width);
						y = random.NextDouble() * (LoadingOverlay.Height - textField.Height);

						// Set collision flag to true
						collisionDetected = true;
						break;
					}
				}
			}
		}

		// Set the position and font size
		AbsoluteLayout.SetLayoutBounds(textField, new Rect(x, y, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));

		MainThread.BeginInvokeOnMainThread(() =>
		{
			LoadingOverlay.Add(textField);			
		});

		await textField.FadeTo(1, 400);
		
    }

    private void Mode_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "SelectedMode")
		{
			// Do something when SelectedMode changes
			VisualStateManager.GoToState(InputUI, ModeSelector.SelectedMode);
		}
	}

	void PointerGestureRecognizer_PointerEntered(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		// Assuming sender is of type that has TargetLanguageTerm property
		if ((sender as Element).BindingContext is VocabWord obj)
		{
			PopOverLabel.Text = obj.TargetLanguageTerm;
			PopOverLabel.IsVisible = true;

			var p = e.GetPosition(Content);

			// Set the position of the label
			PopOverLabel.TranslationX = p.Value.X;
			PopOverLabel.TranslationY = p.Value.Y;
		}
	}
		
	void PointerGestureRecognizer_PointerExited(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		PopOverLabel.Text = "";
		PopOverLabel.IsVisible = false;
	}

	void PointerGestureRecognizer_PointerMoved(System.Object sender, Microsoft.Maui.Controls.PointerEventArgs e)
	{
		var p = e.GetPosition(Content);

		// Set the position of the label
		PopOverLabel.TranslationX = p.Value.X;
		PopOverLabel.TranslationY = p.Value.Y;
	}

}