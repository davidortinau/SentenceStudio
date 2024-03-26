namespace SentenceStudio;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		BindingContext = new AppShellModel();
	}
}
