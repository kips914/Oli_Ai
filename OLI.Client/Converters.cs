using System.Globalization;
using System.Windows;
using System.Windows.Data;
using OLI.Core;

namespace OLI.Client;

/// <summary>user → right, assistant → left, system (status notes) → center.</summary>
public sealed class RoleToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "user" => HorizontalAlignment.Right,
            "system" => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Left,
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Label above each bubble: "Вы" / "OLI" / none for status notes.</summary>
public sealed class RoleToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "user" => Localization.Current == OliLanguage.English ? "You" : "Вы",
            "assistant" => "OLI",
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a chat role to the bubble background brush resource key.</summary>
public sealed class RoleToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "user" => "AccentBrush",
            "system" => "BgInputBrush",
            _ => "BubbleAssistantBrush",
        };
        return Application.Current.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Left-aligned rows get the assistant bubble style, user rows the accent style.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Number → "x, y" percent width for the workspace usage bar.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        var full = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) ? w : 240;
        return Math.Clamp(full * percent / 100.0, 0, full);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
