namespace SentenceStudio.Pages.Writing;
public class AnswerTemplateSelector : DataTemplateSelector
{
    public DataTemplate DesktopTemplate { get; set; }
    public DataTemplate MobileTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        return DeviceInfo.Idiom == DeviceIdiom.Desktop ? DesktopTemplate : MobileTemplate;
    }
}