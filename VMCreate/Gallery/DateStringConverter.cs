using System;
using System.Globalization;
using System.Windows.Data;

namespace VMCreate
{
    /// <summary>
    /// Converts an ISO 8601 (or other parseable) date string to a formatted
    /// local-time string (e.g. "2025-03-06 14:32").  Falls back to the raw
    /// value when parsing fails.
    /// </summary>
    public class DateStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string dateString && !string.IsNullOrWhiteSpace(dateString))
            {
                if (DateTimeOffset.TryParse(dateString, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dto))
                {
                    return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture);
                }

                // Couldn't parse — return as-is
                return dateString;
            }

            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
