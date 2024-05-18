using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using SentenceStudio.Messages;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Pages.Vocabulary;
using SentenceStudio.Services;
using Sharpnado.Tasks;

namespace SentenceStudio;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		Debug.WriteLine($"AppStart Culture: {CultureInfo.CurrentUICulture.Name}");

		// CultureInfo.CurrentUICulture = new CultureInfo( "ko-KR", false );

		// Debug.WriteLine($"New Culture: {CultureInfo.CurrentUICulture.Name}");

		MainPage = new AppShell();     

		// Register a message in some module
		WeakReferenceMessenger.Default.Register<ConnectivityChangedMessage>(this, async (r, m) =>
		{
			if(!m.Value)
				await Shell.Current.CurrentPage.DisplayAlert("No Internet Connection", "Please connect to the internet to use this feature", "OK");
		});
        
	}

    

	protected override void OnStart()
	{
		
	}

	protected override void OnSleep()
	{
	}

	protected override void OnResume()
	{
	}
}
