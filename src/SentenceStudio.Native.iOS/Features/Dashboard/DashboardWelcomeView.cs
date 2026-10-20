using System.Collections.Immutable;
using System.Globalization;
using Microsoft.SwiftUI;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Native.iOS.Platform;
using SentenceStudio.Services.Progress;
using Views = Microsoft.SwiftUI.SwiftUI;

using SentenceStudio.Native.iOS.Styling;

namespace SentenceStudio.Native.iOS.Features.Dashboard;

internal static class DashboardWelcomeView
{
    internal static List<SwiftView> Sections(
        SourceUiStyle style, bool busy, Func<string, string> text,
        Action createStarter, bool starterBusy, string? starterErrorKey,
        Action addResource, bool isTablet)
    {
        var items = new List<SwiftView>();
        var introduction = Views.VStack(
            Views.Image("translate")
                .WithForegroundColor(style.Appearance.Primary.Light,
                    style.Appearance.Primary.Dark),
            style.Text(Views.Text(text("Dashboard_NewUserTitle")),
                    SourceTextRole.Title1, sourceHeading: true)
                .WithAccessibilityIdentifier("native.dashboard.newuser.title"),
            style.Text(Views.Text(text("Dashboard_NewUserDescription")),
                    SourceTextRole.Body1, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier("native.dashboard.newuser.description"))
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
        introduction.Spacing = SourceUiLayout.ItemGap;
        items.Add(introduction);
        if (starterErrorKey is not null)
            items.Add(style.Alert(
                    style.Text(Views.Text(text(starterErrorKey)),
                        SourceTextRole.Body2),
                    success: false)
                .WithAccessibilityIdentifier("native.dashboard.newuser.error"));
        var create = style.PrimaryButton(
                text(starterBusy
                    ? "Dashboard_SettingUp" : "Dashboard_CreateStarterResource"),
                createStarter, fullWidth: true)
            .WithAccessibilityIdentifier("native.dashboard.newuser.starter");
        SourceUiStyle.SetCustomButtonAvailability(create, style.Appearance,
            SourceButtonRole.Primary, !busy && !starterBusy);
        var starter = Views.VStack(
            Views.Image("paperplane.fill")
                .WithForegroundColor(style.Appearance.Primary.Light,
                    style.Appearance.Primary.Dark),
            style.Text(Views.Text(text("Dashboard_QuickStart")),
                SourceTextRole.Title3, sourceHeading: true),
            style.Text(Views.Text(text("NativeDashboard_QuickStartDescription")),
                SourceTextRole.Small, SourceTextTone.Secondary),
            create);
        starter.Spacing = SourceUiLayout.ItemGap;
        var addButton = style.SecondaryButton(
                text("Dashboard_AddAResource"), addResource,
                fullWidth: true)
            .WithAccessibilityIdentifier("native.dashboard.newuser.add");
        SourceUiStyle.SetCustomButtonAvailability(addButton, style.Appearance,
            SourceButtonRole.FilledSecondary, !busy && !starterBusy);
        var add = Views.VStack(
            Views.Image("square.and.pencil")
                .WithForegroundColor(style.Appearance.Primary.Light,
                    style.Appearance.Primary.Dark),
            style.Text(Views.Text(text("Dashboard_CreateYourOwn")),
                SourceTextRole.Title3, sourceHeading: true),
            style.Text(Views.Text(text("Dashboard_CreateYourOwnDescription")),
                SourceTextRole.Small, SourceTextTone.Secondary),
            addButton);
        add.Spacing = SourceUiLayout.ItemGap;
        var starterCard = style.Card(
                starter.WithPadding(SourceUiLayout.LargeCardInset))
            .WithAccessibilityIdentifier("native.dashboard.newuser.starter.card");
        var addCard = style.Card(add.WithPadding(SourceUiLayout.LargeCardInset))
            .WithAccessibilityIdentifier("native.dashboard.newuser.add.card");
        var choices = isTablet
            ? Views.HStack(starterCard, addCard)
            : Views.VStack(starterCard, addCard);
        choices.Spacing = SourceUiLayout.PageGap;
        items.Add(choices.WithFlexibleWidth());
        return items;
    }
}
