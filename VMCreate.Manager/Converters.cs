using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VMCreate.Manager.Inventory;
using Wpf.Ui.Controls;

namespace VMCreate.Manager
{
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class InverseNullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value == null ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>"#RRGGBB" to a brush; null/invalid falls back to the theme's primary text colour.</summary>
    public sealed class HexToBrushConverter : IValueConverter
    {
        private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && hex.Length > 0)
            {
                if (Cache.TryGetValue(hex, out var cached))
                    return cached;
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                    brush.Freeze();
                    return Cache[hex] = brush;
                }
                catch (FormatException)
                {
                }
            }
            return Application.Current.TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.White;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Grid group header text for a folder path group; the empty group holds unfiled VMs.</summary>
    public sealed class FolderGroupNameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string path && path.Length > 0 ? path.Replace("/", "  ›  ") : "Not in a folder";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Machine icon per VM state. ConverterParameter "Badge" returns the small
    /// overlay symbol instead (saved / paused), or Collapsed visibility when
    /// the target is Visibility and the state has no badge.
    /// </summary>
    public sealed class VmStateToSymbolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var state = value is VmState s ? s : VmState.Unknown;
            if (parameter as string == "Badge")
            {
                SymbolRegular? badge = state switch
                {
                    VmState.Saved => SymbolRegular.Save16,
                    VmState.Paused => SymbolRegular.Pause16,
                    _ => null,
                };
                if (targetType == typeof(Visibility))
                    return badge == null ? Visibility.Collapsed : Visibility.Visible;
                return badge ?? SymbolRegular.Empty;
            }
            // Only symbols verified to render: WPF-UI 4.3's font has no glyph for
            // DesktopOff* or DesktopArrowDown* (they draw nothing) even though
            // the enum lists them. Off is told apart by the grey colour instead.
            return state switch
            {
                VmState.Running => SymbolRegular.DesktopPulse20,
                VmState.Off or VmState.Saved or VmState.Paused or VmState.Unknown => SymbolRegular.Desktop20,
                _ => SymbolRegular.DesktopSync20, // starting, stopping, saving, …
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class VmStateToBrushConverter : IValueConverter
    {
        private static readonly Brush Running = Frozen(Color.FromRgb(0x4C, 0xC2, 0x4C));
        private static readonly Brush Transitional = Frozen(Color.FromRgb(0xE8, 0xB3, 0x3A));
        private static readonly Brush Saved = Frozen(Color.FromRgb(0x5B, 0x9B, 0xE6));
        private static readonly Brush Idle = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            VmState.Running => Running,
            VmState.Saved => Saved,
            VmState.Off or VmState.Unknown => Idle,
            _ => Transitional,
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
