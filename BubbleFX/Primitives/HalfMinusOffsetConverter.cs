using System;
using System.Globalization;
using System.Windows.Data;

namespace VisualBuffer.BubbleFX.Primitives
{
    /// <summary>
    /// Возвращает value/2 - offset.
    /// value ожидается как double (Diameter), offset берётся из:
    /// 1) параметра конвертера (string/double), если передан,
    /// 2) либо из свойства <see cref="Offset"/> (для использования без параметра).
    /// Результат неотрицательный (срезаем до 0).
    /// </summary>
    public sealed class HalfMinusOffsetConverter : IValueConverter
    {
        /// <summary>
        /// Смещение по радиусу, если параметр не задан.
        /// </summary>
        public double Offset { get; set; } = 0.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var d = ToDouble(value);
            var off = parameter is null ? Offset : ToDouble(parameter);
            var result = (d * 0.5) - off;
            return result < 0 ? 0.0 : result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static double ToDouble(object v)
        {
            if (v is double dd) return dd;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0.0;
        }
    }
}
