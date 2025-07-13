using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace VMCreateVM
{
    public class ImageSourceConverter : IValueConverter
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string uriString && !string.IsNullOrWhiteSpace(uriString))
            {
                Uri uri;
                try
                {
                    uri = new Uri(uriString, UriKind.RelativeOrAbsolute);
                    if (!uri.IsAbsoluteUri)
                    {
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        uri = new Uri(Path.Combine(baseDir, uriString));
                    }
                }
                catch
                {
                    return null;
                }

                string extension = Path.GetExtension(uri.LocalPath);
                bool isSvg = extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);

                if (isSvg)
                {
                    var settings = new WpfDrawingSettings
                    {
                        IncludeRuntime = true,
                        TextAsGeometry = false,
                        OptimizePath = true
                    };

                    DrawingGroup drawing = null;

                    if (uri.IsFile)
                    {
                        try
                        {
                            using (var reader = new FileSvgReader(settings))
                            {
                                drawing = reader.Read(uri);
                            }
                        }
                        catch
                        {
                            return null;
                        }
                    }
                    else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    {
                        try
                        {
                            using (var stream = _httpClient.GetStreamAsync(uri).Result)
                            using (var reader = new FileSvgReader(settings))
                            {
                                drawing = reader.Read(stream);
                            }
                        }
                        catch
                        {
                            return null;
                        }
                    }

                    if (drawing != null)
                    {
                        return new DrawingImage(drawing);
                    }
                }
                else
                {
                    try
                    {
                        return new BitmapImage(uri);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}