using System;
using System.Reflection;

namespace VMCreate.Gallery
{
    /// <summary>
    /// Provides product version and User-Agent string derived from the assembly version.
    /// </summary>
    public static class ProductInfo
    {
        private static readonly Lazy<string> _version = new(() =>
        {
            var attr = typeof(ProductInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            // InformationalVersion may include "+sha" suffix; strip it for display
            var raw = attr?.InformationalVersion ?? "0.0.0";
            var plusIndex = raw.IndexOf('+');
            return plusIndex >= 0 ? raw[..plusIndex] : raw;
        });

        /// <summary>SemVer version string (e.g. "1.0.0" or "1.0.0-alpha.0.1").</summary>
        public static string Version => _version.Value;

        /// <summary>Full informational version including git SHA (e.g. "1.0.0+abc1234").</summary>
        public static string InformationalVersion =>
            typeof(ProductInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0";

        /// <summary>User-Agent header value for HTTP requests (e.g. "VMCreate/1.0.0").</summary>
        public static string UserAgent => $"VMCreate/{Version}";
    }
}
