using Microsoft.Maui.Controls;
using System;
using System.Globalization;
using Fonts;

namespace SentenceStudio.Pages.Clozure
{
    public class UserActivityToFontImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if(value is null)
            {
                return new FontImageSource{
                    Glyph = FontAwesome.Circle,
                    FontFamily = "FontAwesome",
                    Color = (Color)Application.Current.Resources["Gray100"],
                    Size = 14
                };
            }else{
                UserActivity ua = value as UserActivity;
                return new FontImageSource
                {
                    Glyph = ua.IsCorrect ? FontAwesome.CheckCircle : FontAwesome.TimesCircle,
                    FontFamily = "FontAwesome",
                    Color = ua.IsCorrect ? (Color)Application.Current.Resources["Primary"] : (Color)Application.Current.Resources["Gray400"],
                    Size = 14
                };
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}