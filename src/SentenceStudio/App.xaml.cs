using System.Globalization;
using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Vocabulary;

namespace SentenceStudio;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		CultureInfo.CurrentUICulture = new CultureInfo( "ko", false );

		MainPage = new AppShell();
	}
}
