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
		try
		{
			_logger = logger;
			_logger.LogInformation("App constructor starting");
			Console.WriteLine("App constructor starting");
			_logger.LogDebug($"App constructor stack trace: {Environment.StackTrace}");
			Console.WriteLine($"App constructor stack trace: {Environment.StackTrace}");
			
			InitializeComponent();

			_userProfileRepository = serviceProvider.GetRequiredService<UserProfileRepository>();
			
			_logger.LogInformation("Ahoy! The app be starting! 🏴‍☠️");
			Console.WriteLine("Ahoy! The app be starting! 🏴‍☠️");
			_logger.LogDebug("Debug logging enabled");
			Console.WriteLine("Debug logging enabled");
			_logger.LogDebug($"App startup stack trace: {Environment.StackTrace}");
			Console.WriteLine($"App startup stack trace: {Environment.StackTrace}");

			Console.WriteLine($"AppStart Culture: {CultureInfo.CurrentUICulture.Name}");
			Console.WriteLine($"Manufacturer: {DeviceInfo.Model}");

			// Set the initial culture from user profile (will run asynchronously)
			InitializeUserCulture();
			
			_logger.LogInformation("App constructor completed successfully");
			Console.WriteLine("App constructor completed successfully");
		}
		catch (Exception ex)
		{
			if (_logger != null)
			{
				_logger.LogError(ex, "Exception in App constructor");
				_logger.LogError($"Constructor exception stack trace: {Environment.StackTrace}");
			}
			Console.WriteLine($"Exception in App constructor: {ex}");
			Console.WriteLine($"Constructor exception stack trace: {Environment.StackTrace}");
			throw;
		}
	}
	
	private async Task InitializeUserCulture()
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
				Console.WriteLine($"App culture set to {culture.Name} from user profile");
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to initialize user culture");
			Console.WriteLine($"Failed to initialize user culture: {ex}");
			_logger.LogError($"Stack trace at culture init failure: {Environment.StackTrace}");
			Console.WriteLine($"Stack trace at culture init failure: {Environment.StackTrace}");
		}
	}
}

public abstract class MauiReactorApplication : ReactorApplication<AppShell>
{
	public MauiReactorApplication(IServiceProvider serviceProvider)
		: base(serviceProvider)
	{
		try
		{
			Console.WriteLine($"MauiReactorApplication constructor starting");
			Console.WriteLine($"MauiReactor constructor stack trace: {Environment.StackTrace}");
			
			this.UseTheme<ApplicationTheme>();
			
			Console.WriteLine($"MauiReactorApplication constructor completed");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Exception in MauiReactorApplication constructor: {ex}");
			Console.WriteLine($"MauiReactor exception stack trace: {Environment.StackTrace}");
			throw;
		}
	}

    protected override MauiControls.Window CreateWindow(IActivationState activationState)
    {
		try
		{
			Console.WriteLine("<<<<< WINDOW >>>>>");
			Console.WriteLine($"CreateWindow called");
			Console.WriteLine($"CreateWindow stack trace: {Environment.StackTrace}");
			Console.WriteLine($"ActivationState: {activationState}");
			
			var window = base.CreateWindow(activationState);
			
			Console.WriteLine($"CreateWindow completed successfully");
			return window;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Exception in CreateWindow: {ex}");
			Console.WriteLine($"CreateWindow exception stack trace: {Environment.StackTrace}");
			throw;
		}
    }
}
