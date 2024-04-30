using SentenceStudio;

namespace SentenceStudio.Pages.Account;

public partial class UserProfilePage : ContentPage
{
	public UserProfilePage(UserProfilePageModel model)
	{
		InitializeComponent();

		BindingContext = model;
	}
}