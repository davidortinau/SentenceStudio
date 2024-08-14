using Microsoft.Maui.Controls;
using System;
using System.Globalization;

namespace SentenceStudio.Pages.Writing
{
    public class BoolToReturnTypeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? ReturnType.Next : ReturnType.Done;
            }

            throw new ArgumentException("Value must be of type bool", nameof(value));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}