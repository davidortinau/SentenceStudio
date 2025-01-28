using System.Globalization;
using SentenceStudio.Resources.Styles;

namespace SentenceStudio;

public partial class App : MauiReactorApplication
{
	public App(IServiceProvider serviceProvider)
        : base(serviceProvider)
	{
		InitializeComponent();

		Debug.WriteLine($"AppStart Culture: {CultureInfo.CurrentUICulture.Name}");
		Debug.WriteLine($"Manufacturer: {DeviceInfo.Model}");

		if(DeviceInfo.Model.Contains("NoteAir3C", StringComparison.CurrentCultureIgnoreCase)) // TODO expand this to detect any eInk device
		{
			Application.Current.Resources.MergedDictionaries.Add(new HighContrastColors());
		}
		else
		{
			Application.Current.Resources.MergedDictionaries.Add(new Resources.Styles.AppColors());
		}
		
		Application.Current.Resources.MergedDictionaries.Add(new Resources.Styles.Fluent());
		Application.Current.Resources.MergedDictionaries.Add(new Resources.Styles.Styles());
		Application.Current.Resources.MergedDictionaries.Add(new Resources.Styles.Converters());

		// Application.Current.Resources.MergedDictionaries.Add(mergedResources);

		// CultureInfo.CurrentUICulture = new CultureInfo( "ko-KR", false );

		// Debug.WriteLine($"New Culture: {CultureInfo.CurrentUICulture.Name}");

		

		// Register a message in some module
		// WeakReferenceMessenger.Default.Register<ConnectivityChangedMessage>(this, async (r, m) =>
		// {
		// 	if(!m.Value)
		// 		await Shell.Current.CurrentPage.DisplayAlert("No Internet Connection", "Please connect to the internet to use this feature", "OK");
		// });
        
	}
    
    // protected override Window CreateWindow(IActivationState? activationState)
	// {
	// 	bool isOnboarded = Preferences.Default.ContainsKey("is_onboarded");
        
    //     if (!isOnboarded)
    //     {
    //         return new Window( new OnboardingPage(activationState.Context.Services.GetService<OnboardingPageModel>()) );
    //     }
	// 	else
	// 	{
	// 		return new Window( new AppShell(activationState.Context.Services.GetService<AppShellModel>()) );
	// 	}
	// }
}

public abstract class MauiReactorApplication : ReactorApplication<AppShell>
{
    public MauiReactorApplication(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
        this.UseTheme<ApplicationTheme>();
    }
}
