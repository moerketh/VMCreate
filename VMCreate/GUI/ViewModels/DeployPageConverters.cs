using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace VMCreate
{
    /// <summary>Converts <see cref="DeploymentPhaseStatus"/> to a foreground <see cref="Brush"/>.</summary>
    public class PhaseStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DeploymentPhaseStatus status)
                return DependencyProperty.UnsetValue;

            string resourceKey = status switch
            {
                DeploymentPhaseStatus.Completed => "SystemFillColorSuccessBrush",
                DeploymentPhaseStatus.Active    => "AccentBrush",
                DeploymentPhaseStatus.Failed    => "SystemFillColorCriticalBrush",
                _                               => "TextFillColorSecondaryBrush"
            };

            return Application.Current.TryFindResource(resourceKey) as Brush
                ?? Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush
                ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts <see cref="DeploymentPhaseStatus"/> to an appropriate <see cref="SymbolRegular"/>.
    /// Completed → checkmark, Failed → error circle, otherwise original icon (passed as parameter or fallback).
    /// Because multi-binding with Icon isn't straightforward, the card template will bind
    /// the icon directly from the DataContext via a multi-converter-free approach.
    /// This converter is used when the status overrides the default icon.
    /// </summary>
    public class PhaseStatusToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not DeploymentPhaseStatus status)
                return SymbolRegular.Circle24;

            return status switch
            {
                DeploymentPhaseStatus.Completed => SymbolRegular.CheckmarkCircle24,
                DeploymentPhaseStatus.Failed    => SymbolRegular.ErrorCircle24,
                DeploymentPhaseStatus.Skipped   => SymbolRegular.SubtractCircle24,
                _                               => SymbolRegular.Circle24 // placeholder; overridden by multi-binding
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Returns Visibility.Visible when status is Active and IsIndeterminate is false (deterministic progress).</summary>
    public class PhaseShowBarConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This is bound to Status; the progress bar is shown only for Active + non-indeterminate.
            // The IsIndeterminate check is handled separately in XAML.
            if (value is DeploymentPhaseStatus status && status == DeploymentPhaseStatus.Active)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Returns true when the phase is Active.</summary>
    public class PhaseActiveConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DeploymentPhaseStatus status && status == DeploymentPhaseStatus.Active;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Collapses the element when the bound value is null or empty string.</summary>
    public class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s && !string.IsNullOrEmpty(s))
                return Visibility.Visible;
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Converts an <see cref="int"/> indent level to a <see cref="Thickness"/> with left margin (level × 32px) and fixed bottom 8px.</summary>
    public class IndentLevelToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int level = value is int i ? i : 0;
            return new Thickness(level * 32, 0, 0, 8);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Inverts a boolean and converts to Visibility. True→Collapsed, False→Visible.</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
