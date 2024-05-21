using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Microsoft.Maui.Platform;
using SentenceStudio.Models;
using SentenceStudio.Services;

#if IOS
using UIKit;
using Foundation;
#endif

namespace SentenceStudio.Pages.Lesson;

public partial class WritingPage : ContentPage
{
	WritingPageModel _model;
    private double previousHeight;

    public WritingPage(WritingPageModel model)
	{
		InitializeComponent();
		BindingContext = _model = model;

#if IOS
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillShowNotification, KeyboardWillShow);
		NSNotificationCenter.DefaultCenter.AddObserver(UIKeyboard.WillHideNotification, KeyboardWillHide);
#endif
		
	}

#if IOS
	private void KeyboardWillShow(NSNotification notification)
	{
		// Handle keyboard will show event here
		InputUI.Margin = new Thickness(0, 0, 0, 40);
	}

	private void KeyboardWillHide(NSNotification notification)
	{
		// Handle keyboard will hide event here
		InputUI.Margin = new Thickness(0, 0, 0, 0);
	}
#endif

}