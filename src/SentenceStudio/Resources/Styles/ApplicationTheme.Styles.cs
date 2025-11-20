using MauiReactor.Shapes;
using ReactorTheme.Styles;

namespace SentenceStudio.Resources.Styles;

partial class MyTheme
{
    partial void ApplyStyles()
    {
        SfTextInputLayoutStyles.Default = _ => _
            .ContainerType(Syncfusion.Maui.Toolkit.TextInputLayout.ContainerType.Filled)
            .OutlineCornerRadius(0)
            .ContainerBackground(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground);

        ActivityIndicatorStyles.Default = _ =>
            _.Color(IsLightTheme ? HighlightDarkest : White);

        IndicatorViewStyles.Default = _ => _
            .IndicatorColor(IsLightTheme ? Gray200 : Gray500)
            .SelectedIndicatorColor(IsLightTheme ? Gray950 : Gray100);

        BorderStyles.Default = _ => _
            .Stroke(IsLightTheme ? Gray200 : Gray500)
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BoxViewStyles.Default = _ => _
            .BackgroundColor(IsLightTheme ? Gray950 : Gray200);

        ButtonStyles.Default = _ => _
            .TextColor(IsLightTheme ? White : PrimaryDarkText)
            .BackgroundColor(IsLightTheme ? HighlightDarkest : PrimaryDark)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .BorderWidth(0)
            .CornerRadius(8)
            .Padding(14, 10)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Button.TextColorProperty, IsLightTheme ? Gray950 : Gray200)
            .VisualState("CommonStates", "Disable", MauiControls.Button.BackgroundColorProperty, IsLightTheme ? Gray200 : Gray600);

        ButtonStyles.Themes[Secondary] = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .BorderWidth(0)
            .CornerRadius(8)
            .Padding(14, 10)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Button.TextColorProperty, IsLightTheme ? Gray200 : Gray500)
            .VisualState("CommonStates", "Disable", MauiControls.Button.BackgroundColorProperty, IsLightTheme ? Gray300 : Gray600);

        CheckBoxStyles.Default = _ => _
            .Color(IsLightTheme ? HighlightDarkest : White)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.CheckBox.ColorProperty, IsLightTheme ? Gray300 : Gray600);

        DatePickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.DatePicker.TextColorProperty, IsLightTheme ? Gray200 : Gray500);

        EditorStyles.Default = _ => _
            .TextColor(IsLightTheme ? Black : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .PlaceholderColor(IsLightTheme ? Gray200 : Gray500)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Editor.TextColorProperty, IsLightTheme ? Gray300 : Gray600);


        EntryStyles.Default = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(DeviceInfo.Current.Idiom == DeviceIdiom.Desktop ? 24 : 18)
            .PlaceholderColor(IsLightTheme ? Gray200 : Gray500)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Entry.TextColorProperty, IsLightTheme ? Gray300 : Gray600);


        ImageButtonStyles.Default = _ => _
            .Opacity(1)
            .BorderColor(Colors.Transparent)
            .BorderWidth(0)
            .CornerRadius(0)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.ImageButton.OpacityProperty, 0.5);

        LabelStyles.Default = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(17)
            .LineHeight(1.29)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Headline] = _ => _
            .TextColor(IsLightTheme ? MidnightBlue : White)
            .FontSize(32)
            .HorizontalOptions(LayoutOptions.Center)
            .HorizontalTextAlignment(TextAlignment.Center)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[SubHeadline] = _ => _
            .TextColor(IsLightTheme ? MidnightBlue : White)
            .FontSize(24)
            .HorizontalOptions(LayoutOptions.Center)
            .HorizontalTextAlignment(TextAlignment.Center)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Caption2] = _ => _
            .FontSize(12)
            .LineHeight(1.33)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Caption1] = _ => _
            .FontSize(13)
            .LineHeight(1.38)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Caption1Strong] = _ => _
            .FontSize(13)
            .LineHeight(1.38)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Body2] = _ => _
            .FontSize(15)
            .LineHeight(1.33)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Body2Strong] = _ => _
            .FontSize(15)
            .LineHeight(1.33)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Body1] = _ => _
            .FontSize(17)
            .LineHeight(1.29)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Body1Strong] = _ => _
            .FontSize(17)
            .LineHeight(1.29)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Title3] = _ => _
            .FontSize(20)
            .LineHeight(1.25)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Title2] = _ => _
            .FontSize(22)
            .LineHeight(1.27)
            .FontFamily(DeviceInfo.Platform == DevicePlatform.WinUI ? "SegoeSemibold" : DeviceInfo.Platform == DevicePlatform.iOS || DeviceInfo.Platform == DevicePlatform.MacCatalyst ? ".SFUI-SemiBold" : "")
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.Android ? FontAttributes.Bold : FontAttributes.None)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Title1] = _ => _
            .FontSize(28)
            .LineHeight(1.21)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[LargeTitle] = _ => _
            .FontSize(34)
            .LineHeight(1.21)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        LabelStyles.Themes[Display] = _ => _
            .FontSize(60)
            .LineHeight(1.17)
            .FontAttributes(DeviceInfo.Platform == DevicePlatform.WinUI ? FontAttributes.None : FontAttributes.Bold)
            .VisualState("CommonStates", "Disable", MauiControls.Label.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        BorderStyles.Default = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BorderStyles.Themes[CardStyle] = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BorderStyles.Themes[InputWrapper] = _ => _
            .StrokeShape(new RoundRectangle().CornerRadius(20))
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .StrokeThickness(0)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        BoxViewStyles.Themes[ShimmerCustomViewStyle] = _ => _
            .BackgroundColor(Colors.Gray)
            .HorizontalOptions(LayoutOptions.Fill)
            .VerticalOptions(LayoutOptions.Center);

        ImageButtonStyles.Default = _ => _
            .BackgroundColor(Colors.Transparent);

        LayoutStyles.Themes[Surface1] = _ => _
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .Padding(DeviceInfo.Idiom == DeviceIdiom.Desktop ? 20 : 15);

        PickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .TitleColor(IsLightTheme ? Gray900 : Gray200)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(DeviceIdiom.Desktop == DeviceInfo.Idiom ? 24 : 18)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.Picker.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Picker.TitleColorProperty, IsLightTheme ? Gray300 : Gray600);

        ProgressBarStyles.Default = _ => _
            .ProgressColor(IsLightTheme ? HighlightDarkest : White)
            .VisualState("CommonStates", "Disable", MauiControls.ProgressBar.ProgressColorProperty, IsLightTheme ? Gray300 : Gray600);

        RadioButtonStyles.Default = _ => _
            .BackgroundColor(Colors.Transparent)
            .TextColor(IsLightTheme ? Black : White)
            .FontFamily("SegoeRegular")
            .FontSize(DeviceIdiom.Desktop == DeviceInfo.Idiom ? 24 : 18)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.RadioButton.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        RefreshViewStyles.Default = _ => _
            .RefreshColor(IsLightTheme ? Gray900 : Gray200);

        SearchBarStyles.Default = _ => _
            .TextColor(IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .Background(IsLightTheme ? LightSecondaryBackground : DarkSecondaryBackground)
            .PlaceholderColor(Gray500)
            .CancelButtonColor(Gray500)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.SearchBar.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.SearchBar.PlaceholderColorProperty, IsLightTheme ? Gray300 : Gray600);

        //SearchHandlerStyles.Default = _ => _
        //    .TextColor(IsLightTheme ? Gray900 : White)
        //    .PlaceholderColor(Gray500)
        //    .BackgroundColor(Colors.Transparent)
        //    .FontFamily("SegoeRegular")
        //    .FontSize(14)
        //    .VisualState("CommonStates", "Disable", MauiControls.SearchHandler.TextColorProperty, IsLightTheme ? Gray300 : Gray600)
        //    .VisualState("CommonStates", "Disable", MauiControls.SearchHandler.PlaceholderColorProperty, IsLightTheme ? Gray300 : Gray600);

        ShadowStyles.Default = _ => _
            .Radius(15)
            .Opacity(0.5f)
            .Brush(IsLightTheme ? White : White)
            .Offset(new Point(10, 10));

        SliderStyles.Default = _ => _
            .MinimumTrackColor(IsLightTheme ? HighlightDarkest : White)
            .MaximumTrackColor(IsLightTheme ? Gray200 : Gray600)
            .ThumbColor(IsLightTheme ? HighlightDarkest : White)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.MinimumTrackColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.MaximumTrackColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Slider.ThumbColorProperty, IsLightTheme ? Gray300 : Gray600);

        SwipeItemStyles.Default = _ => _
            .BackgroundColor(IsLightTheme ? White : Black);

        SwitchStyles.Default = _ => _
            .OnColor(IsLightTheme ? HighlightDarkest : White)
            .ThumbColor(White)
            .VisualState("CommonStates", "Disable", MauiControls.Switch.OnColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "Disable", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? Gray300 : Gray600)
            .VisualState("CommonStates", "On", MauiControls.Switch.OnColorProperty, IsLightTheme ? Secondary : Gray200)
            .VisualState("CommonStates", "On", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? HighlightDarkest : White)
            .VisualState("CommonStates", "Off", MauiControls.Switch.ThumbColorProperty, IsLightTheme ? Gray400 : Gray500);


        TimePickerStyles.Default = _ => _
            .TextColor(IsLightTheme ? Gray900 : White)
            .BackgroundColor(Colors.Transparent)
            .FontFamily("SegoeRegular")
            .FontSize(14)
            .MinimumHeightRequest(44)
            .MinimumWidthRequest(44)
            .VisualState("CommonStates", "Disable", MauiControls.TimePicker.TextColorProperty, IsLightTheme ? Gray300 : Gray600);

        TitleBarStyles.Default = _ => _
            .MinimumHeightRequest(32)
            .VisualState("TitleActiveStates", "TitleBarTitleActive", MauiControls.TitleBar.BackgroundColorProperty, Colors.Transparent)
            .VisualState("TitleActiveStates", "TitleBarTitleActive", MauiControls.TitleBar.ForegroundColorProperty, IsLightTheme ? Black : White)
            .VisualState("TitleActiveStates", "TitleBarTitleInactive", MauiControls.TitleBar.BackgroundColorProperty, IsLightTheme ? White : Black)
            .VisualState("TitleActiveStates", "TitleBarTitleInactive", MauiControls.TitleBar.ForegroundColorProperty, IsLightTheme ? Gray400 : Gray500);

        PageStyles.Default = _ => _
            .Padding(0)
            // .Set(MauiControls.Layout.SafeAreaEdgesProperty, new SafeAreaEdges(SafeAreaRegions.Default, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None))
            // .Set(Layout.SafeAreaEdgesProperty,
            //         (DeviceDisplay.Current.MainDisplayInfo.Orientation == DisplayOrientation.Portrait)
            //         ? new SafeAreaEdges(SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None)
            //         : new SafeAreaEdges(SafeAreaRegions.All, SafeAreaRegions.None, SafeAreaRegions.None, SafeAreaRegions.None))
            .BackgroundColor(IsLightTheme ? LightBackground : DarkBackground);

        ShellStyles.Default = _ => _
            .Set(MauiControls.Shell.BackgroundColorProperty, IsLightTheme ? LightBackground : DarkBackground)
            .Set(MauiControls.Shell.ForegroundColorProperty, IsLightTheme ? Black : SecondaryDarkText)
            .Set(MauiControls.Shell.TitleColorProperty, IsLightTheme ? Black : SecondaryDarkText)
            .Set(MauiControls.Shell.DisabledColorProperty, IsLightTheme ? Gray200 : Gray950)
            .Set(MauiControls.Shell.UnselectedColorProperty, IsLightTheme ? Gray200 : Gray200)
            .Set(MauiControls.Shell.NavBarHasShadowProperty, false)
            .Set(MauiControls.Shell.TabBarBackgroundColorProperty, IsLightTheme ? LightBackground : DarkBackground)
            .Set(MauiControls.Shell.TabBarForegroundColorProperty, IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .Set(MauiControls.Shell.TabBarTitleColorProperty, IsLightTheme ? DarkOnLightBackground : LightOnDarkBackground)
            .Set(MauiControls.Shell.TabBarUnselectedColorProperty, IsLightTheme ? Gray900 : Gray200);

        NavigationPageStyles.Default = _ => _
            .Set(MauiControls.NavigationPage.BarBackgroundColorProperty, IsLightTheme ? White : OffBlack)
            .Set(MauiControls.NavigationPage.BarTextColorProperty, IsLightTheme ? Gray200 : White)
            .Set(MauiControls.NavigationPage.IconColorProperty, IsLightTheme ? Gray200 : White);

        TabbedPageStyles.Default = _ => _
            .Set(MauiControls.TabbedPage.BarBackgroundColorProperty, IsLightTheme ? White : Gray950)
            .Set(MauiControls.TabbedPage.BarTextColorProperty, IsLightTheme ? Magenta : White)
            .Set(MauiControls.TabbedPage.UnselectedTabColorProperty, IsLightTheme ? Gray200 : Gray950)
            .Set(MauiControls.TabbedPage.SelectedTabColorProperty, IsLightTheme ? Gray950 : Gray200);
    }
}
