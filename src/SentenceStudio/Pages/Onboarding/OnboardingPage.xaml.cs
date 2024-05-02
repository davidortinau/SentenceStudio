using SentenceStudio;

namespace SentenceStudio.Pages.Onboarding;

public partial class OnboardingPage : ContentPage
{
	public OnboardingPage(OnboardingPageModel model)
	{
		InitializeComponent();

		BindingContext = model;

	}
}