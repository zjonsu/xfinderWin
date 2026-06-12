using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace XFinder.Views;

/// <summary>bool → Visibility ("invert" 파라미터 지원).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed ("invert"면 반대).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SF Symbol 이름 → Segoe 글리프 문자열.</summary>
public sealed class GlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IconMap.Glyph(value as string ?? "");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Color + 불투명도 파라미터 → SolidColorBrush.</summary>
public sealed class ColorOpacityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color c) return Brushes.Transparent;
        var opacity = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1.0;
        var alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>배율(listScale) 곱 컨버터 — 파라미터 = 기준값.</summary>
public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var scale = value is double d ? d : 1.0;
        var baseValue = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 1.0;
        return scale * baseValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
