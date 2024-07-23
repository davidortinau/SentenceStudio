
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using CommunityToolkit.Maui.Markup;

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

    public FormField()
    {
        ControlTemplate = new ControlTemplate(()=>{
            return new VerticalStackLayout{ Spacing = (Double)Application.Current.Resources["size120"], Children = {
                new Label()
                    .Start()
                    .Bind(Label.TextProperty, nameof(FormField.FieldLabel)),
                new Border{
                    Style = (Style)Application.Current.Resources["InputWrapper"],
                    Content = new ContentPresenter()
                }
            } };
        });
    }

    
}