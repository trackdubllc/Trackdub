using Avalonia.Data.Converters;
using System.Globalization;

namespace DubBench.Converters;

/// <summary>
/// Shared value converters for DubBench views.
/// </summary>
public static class BoolConverters
{
    /// <summary>
    /// Inverts a boolean value.
    /// </summary>
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(v => !v);

    /// <summary>
    /// Returns true when value is not null.
    /// </summary>
    public static readonly IValueConverter IsNotNull = new FuncValueConverter<object?, bool>(v => v is not null);
}

/// <summary>
/// Wraps a function as an <see cref="IValueConverter"/>.
/// </summary>
public sealed class FuncValueConverter<TIn, TOut>(Func<TIn?, TOut> convert) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TIn input ? convert(input) : convert(default);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
