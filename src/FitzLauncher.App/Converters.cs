using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FitzLauncher.App;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        var hasValue = value is not null && value is not string { Length: 0 };
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Two-way binding of a RadioButton to an enum/int value given as ConverterParameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        if (value is not true) return Binding.DoNothing;
        var target = Nullable.GetUnderlyingType(t) ?? t;
        return target.IsEnum ? Enum.Parse(target, p.ToString()!) : System.Convert.ChangeType(p, target, CultureInfo.InvariantCulture);
    }
}

public sealed class ImpactToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Application.Current.FindResource(value?.ToString() switch
    {
        "None" => "Good",
        "Low" => "Accent",
        "Medium" => "Warn",
        _ => "Bad",
    });
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value's text equals ConverterParameter (page switching).</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value?.ToString() == p?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the bound string is empty (placeholder text).</summary>
public sealed class EmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}
