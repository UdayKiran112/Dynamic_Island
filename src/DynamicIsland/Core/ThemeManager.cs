using System.Windows;

namespace DynamicIsland.Core;

/// <summary>Swaps the theme resource dictionary at runtime.</summary>
public static class ThemeManager
{
    private static int _themeIndex = -1;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
        };

        var dictionaries = app.Resources.MergedDictionaries;

        if (_themeIndex >= 0 && _themeIndex < dictionaries.Count)
        {
            dictionaries[_themeIndex] = dictionary;
            return;
        }

        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                source.Contains("Themes/", StringComparison.OrdinalIgnoreCase) &&
                !source.Contains("Styles", StringComparison.OrdinalIgnoreCase) &&
                !source.Contains("Icons", StringComparison.OrdinalIgnoreCase))
            {
                _themeIndex = i;
                dictionaries[i] = dictionary;
                return;
            }
        }

        dictionaries.Insert(0, dictionary);
        _themeIndex = 0;
    }
}
