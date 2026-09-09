using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace MaxLoud
{
    internal static class WpfAssetService
    {
        private static readonly Dictionary<string, BitmapImage> Cache =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        public static BitmapImage LoadImage(string relativePath)
        {
            var fullPath = ResolvePath(relativePath);
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            if (Cache.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            Cache[fullPath] = image;
            return image;
        }

        private static string ResolvePath(string relativePath)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
