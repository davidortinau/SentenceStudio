using System.Globalization;
using Microsoft.Extensions.Logging;
using SentenceStudio.Pages.Onboarding;
using SentenceStudio.Resources.Styles;

namespace SentenceStudio;

public partial class App : MauiReactorApplication
{
	private readonly ILogger<App> _logger;
	private readonly UserProfileRepository _userProfileRepository;
	
	public App(IServiceProvider serviceProvider, ILogger<App> logger)
		: base(serviceProvider)
	{
		InitializeComponent();

		_logger = logger;
        _userProfileRepository = serviceProvider.GetRequiredService<UserProfileRepository>();
        
        _logger.LogInformation("Ahoy! The app be starting! 🏴‍☠️");
        _logger.LogDebug("Debug logging enabled");

		Debug.WriteLine($"AppStart Culture: {CultureInfo.CurrentUICulture.Name}");
		Debug.WriteLine($"Manufacturer: {DeviceInfo.Model}");

		if (DeviceInfo.Model.Contains("NoteAir3C", StringComparison.CurrentCultureIgnoreCase)) // TODO expand this to detect any eInk device
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

		 // Set the initial culture from user profile (will run asynchronously)
		InitializeUserCulture();
	}
	
	private async void InitializeUserCulture()
	{
		try
		{
			var profile = await _userProfileRepository.GetAsync();
			if (profile != null && !string.IsNullOrEmpty(profile.DisplayLanguage))
			{
				// Convert display language name to culture code
				string cultureCode = profile.DisplayLanguage == "Korean" ? "ko-KR" : "en-US";
				var culture = new CultureInfo(cultureCode);
				
				// Set the culture using the LocalizationManager
				LocalizationManager.Instance.SetCulture(culture);
				
				_logger.LogInformation($"App culture set to {culture.Name} from user profile");
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to initialize user culture");
		}
	}
}

public abstract class MauiReactorApplication : ReactorApplication<AppShell>
{
    public MauiReactorApplication(IServiceProvider serviceProvider)
        : base(serviceProvider)
    {
        this.UseTheme<ApplicationTheme>();
    }
}
