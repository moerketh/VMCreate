using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace VMCreate
{
    public class ImageSourceConverter : IValueConverter
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // Cache HTTP image results so each remote URL is only downloaded once per session.
        // Lazy<T> ensures only a single download races for each key even under concurrent bindings.
        private static readonly ConcurrentDictionary<string, Lazy<ImageSource>> _httpImageCache = new();

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
                        var lazy = _httpImageCache.GetOrAdd(
                            uri.OriginalString,
                            key => new Lazy<ImageSource>(() => LoadHttpSvgSync(uri)));
                        return lazy.Value;
                    }

                    if (drawing != null)
                    {
                        return new DrawingImage(drawing);
                    }
                }
                else if (uri.IsFile || uri.Scheme == "pack")
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
                else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    // Download HTTP bitmap images with HttpClient (WPF's built-in
                    // BitmapImage(uri) silently fails for many HTTPS URLs).
                    var lazy = _httpImageCache.GetOrAdd(
                        uri.OriginalString,
                        key => new Lazy<ImageSource>(() => LoadHttpBitmapSync(uri)));
                    return lazy.Value;
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

        private static ImageSource LoadHttpSvgSync(Uri uri)
        {
            try
            {
                return Task.Run(async () =>
                {
                    using var stream = await _httpClient.GetStreamAsync(uri).ConfigureAwait(false);
                    var settings = new WpfDrawingSettings
                    {
                        IncludeRuntime = true,
                        TextAsGeometry = false,
                        OptimizePath = true
                    };
                    using var reader = new FileSvgReader(settings);
                    var drawing = reader.Read(stream);
                    if (drawing != null)
                    {
                        var image = new DrawingImage(drawing);
                        image.Freeze(); // required for cross-thread access
                        return (ImageSource)image;
                    }
                    return null;
                }).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource LoadHttpBitmapSync(Uri uri)
        {
            try
            {
                return Task.Run(async () =>
                {
                    var bytes = await _httpClient.GetByteArrayAsync(uri).ConfigureAwait(false);
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze(); // required for cross-thread access
                    return (ImageSource)bitmap;
                }).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}