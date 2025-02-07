using MauiIcons.SegoeFluent;
using Microsoft.Maui.Controls;

namespace SentenceStudio.Common;

public static class IconExtensions
{
    public static FontImageSource ToFontImageSource(this SegoeFluentIcons icon) => new()
    {
        Glyph = icon.ToString(),
        FontFamily = "FluentIcons",
        Size = 24,
        Color = Colors.Black
    };
} 