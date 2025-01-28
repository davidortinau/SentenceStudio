using System;

namespace SentenceStudio.Resources.Styles;

static class ResourceHelper
{
    public static T GetResource<T>(string key)
    {
        if (Application.Current!.Resources.MergedDictionaries.ElementAt(0)
            .TryGetValue(key, out object? v1))
        {
            return (T)v1;
        }
        if (Application.Current!.Resources.MergedDictionaries.ElementAt(1)
            .TryGetValue(key, out object? v2))
        {
            return (T)v2;
        }

        return (T)Application.Current!.Resources.MergedDictionaries.ElementAt(2)[key];
    }
}