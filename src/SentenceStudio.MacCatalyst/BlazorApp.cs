using Microsoft.Maui.Controls;

namespace SentenceStudio;

public class BlazorApp : Application
{
    public BlazorApp()
    {
        MainPage = new BlazorHostPage();
    }
}
