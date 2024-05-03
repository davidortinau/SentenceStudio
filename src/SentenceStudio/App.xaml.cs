using System.Globalization;
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

		CultureInfo.CurrentUICulture = new CultureInfo( "ko", false );

		MainPage = new AppShell();       
        
		
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
