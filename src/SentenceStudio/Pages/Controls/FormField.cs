
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace SentenceStudio.Pages.Controls;

public class FormField : ContentView
{
    public static readonly BindableProperty FieldLabelProperty = BindableProperty.Create(
        propertyName: "FieldLabel",
        returnType: typeof(string),
        declaringType: typeof(FormField),
        defaultValue: default(string));

    public string FieldLabel
    {
        get { return (string)GetValue(FieldLabelProperty); }
        set { SetValue(FieldLabelProperty, value); }
    }

    
}