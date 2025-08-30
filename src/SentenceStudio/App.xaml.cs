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
			//Console.Writeline("App constructor starting");
			_logger.LogDebug($"App constructor stack trace: {Environment.StackTrace}");
			//Console.Writeline($"App constructor stack trace: {Environment.StackTrace}");
			
			InitializeComponent();

			_userProfileRepository = serviceProvider.GetRequiredService<UserProfileRepository>();
			
			_logger.LogInformation("Ahoy! The app be starting! 🏴‍☠️");
			//Console.Writeline("Ahoy! The app be starting! 🏴‍☠️");
			_logger.LogDebug("Debug logging enabled");
			//Console.Writeline("Debug logging enabled");
			_logger.LogDebug($"App startup stack trace: {Environment.StackTrace}");
			//Console.Writeline($"App startup stack trace: {Environment.StackTrace}");

			//Console.Writeline($"AppStart Culture: {CultureInfo.CurrentUICulture.Name}");
			//Console.Writeline($"Manufacturer: {DeviceInfo.Model}");

			// Set the initial culture from user profile (will run asynchronously)
			InitializeUserCulture();
			
			_logger.LogInformation("App constructor completed successfully");
			//Console.Writeline("App constructor completed successfully");
		}
		catch (Exception ex)
		{
			if (_logger != null)
			{
				_logger.LogError(ex, "Exception in App constructor");
				_logger.LogError($"Constructor exception stack trace: {Environment.StackTrace}");
			}
			//Console.Writeline($"Exception in App constructor: {ex}");
			//Console.Writeline($"Constructor exception stack trace: {Environment.StackTrace}");
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
				//Console.Writeline($"App culture set to {culture.Name} from user profile");
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to initialize user culture");
			//Console.Writeline($"Failed to initialize user culture: {ex}");
			_logger.LogError($"Stack trace at culture init failure: {Environment.StackTrace}");
			//Console.Writeline($"Stack trace at culture init failure: {Environment.StackTrace}");
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
			//Console.Writeline($"MauiReactorApplication constructor starting");
			//Console.Writeline($"MauiReactor constructor stack trace: {Environment.StackTrace}");
			
			this.UseTheme<MyTheme>();
			
			//Console.Writeline($"MauiReactorApplication constructor completed");
		}
		catch (Exception)
		{
			//Console.Writeline($"Exception in MauiReactorApplication constructor: {ex}");
			//Console.Writeline($"MauiReactor exception stack trace: {Environment.StackTrace}");
			throw;
		}
	}

    protected override MauiControls.Window CreateWindow(IActivationState activationState)
    {
		try
		{
			//Console.Writeline("<<<<< WINDOW >>>>>");
			//Console.Writeline($"CreateWindow called");
			//Console.Writeline($"CreateWindow stack trace: {Environment.StackTrace}");
			//Console.Writeline($"ActivationState: {activationState}");
			
			var window = base.CreateWindow(activationState);
			
			//Console.Writeline($"CreateWindow completed successfully");
			return window;
		}
		catch (Exception)
		{
			//Console.Writeline($"Exception in CreateWindow: {ex}");
			//Console.Writeline($"CreateWindow exception stack trace: {Environment.StackTrace}");
			throw;
		}
    }
}
