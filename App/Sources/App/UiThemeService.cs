using System;
using System.Windows;
using System.Windows.Media;
using NeoUI;

namespace MaxLoud
{
    /// <summary>
    /// Adapts the shared NeoUI palette to MaxLoud WPF resources.
    /// </summary>
    internal static class UiThemeService
    {
        public const string LightTheme = NeoThemePalettes.LightTheme;
        public const string DarkTheme = NeoThemePalettes.DarkTheme;

        public static string NormalizeTheme(string theme)
        {
            return NeoThemePalettes.NormalizeTheme(theme);
        }

        public static bool IsDarkTheme(string theme)
        {
            return NeoThemePalettes.IsDarkTheme(theme);
        }

        public static string ToggleTheme(string theme)
        {
            return IsDarkTheme(theme) ? LightTheme : DarkTheme;
        }

        public static void Apply(Application application, string theme)
        {
            if (application == null)
            {
                return;
            }

            var palette = NeoThemePalettes.Get(theme);
            var dark = IsDarkTheme(theme);
            var resources = application.Resources;

            resources["BackgroundBrush"] = Brush(palette.Background);
            resources["SurfaceBrush"] = Brush(palette.Surface);
            resources["RaisedSurfaceBrush"] = Brush(palette.SurfaceRaised);
            resources["TextBrush"] = Brush(palette.Text);
            resources["MutedTextBrush"] = Brush(palette.MutedText);
            resources["BorderBrush"] = Brush(palette.Border);
            resources["PrimaryBrush"] = Brush(palette.Primary);
            resources["PrimarySoftBrush"] = Brush(dark ? "#2638BDF8" : "#1200ACF7");
            resources["DangerBrush"] = Brush(palette.Error);
            resources["WarningBrush"] = Brush(palette.Warning);
            resources["SuccessBrush"] = Brush(palette.Success);
            resources["TitleBarBackgroundBrush"] = Brush(palette.TitleBarBackground);
            resources["TitleBarBorderBrush"] = Brush(palette.TitleBarBorder);
            resources["ButtonHoverBackgroundBrush"] = Brush(dark ? "#25384644" : "#1400ACF7");
            resources["ButtonPressedBackgroundBrush"] = Brush(dark ? "#4038BDF8" : "#2600ACF7");
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
