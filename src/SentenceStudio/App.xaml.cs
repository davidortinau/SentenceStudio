using SentenceStudio.Pages.Dashboard;
using SentenceStudio.Pages.Vocabulary;

namespace SentenceStudio;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();

		MainPage = new AppShell();
	}
}
