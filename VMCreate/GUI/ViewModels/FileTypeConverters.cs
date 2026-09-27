using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VMCreate
{
    /// <summary>
    /// Visibility of the file-type badge in the gallery list. Pass the GalleryItem.
    /// The pill is redundant for pre-installed VHDX images (they carry a
    /// "Pre-installed" badge already) and for native Hyper-V items; both collapse.
    /// </summary>
    public class FileTypePillVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not GalleryItem item)
                return Visibility.Collapsed;

            if (item.IsNativeHyperV)
                return Visibility.Collapsed;

            if (item.IsPreInstalled && string.Equals(item.FileType, "VHDX", StringComparison.OrdinalIgnoreCase))
                return Visibility.Collapsed;

            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
