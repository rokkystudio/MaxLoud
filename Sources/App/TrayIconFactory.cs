using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxLoud
{
    /// <summary>
    /// Creates the monochrome MaxLoud manometer as a native HICON using the WPF drawing stack.
    /// No WinForms or System.Drawing dependency is required.
    /// </summary>
    internal static class TrayIconFactory
    {
        private const int SmCxSmallIcon = 49;
        private const int SmCySmallIcon = 50;
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;

        private static readonly Color ActiveColor =
            Color.FromArgb(255, 238, 238, 238);

        private static readonly Color InactiveColor =
            Color.FromArgb(255, 118, 118, 118);

        /// <summary>
        /// Creates a transparent native HICON sized for the current Windows notification area.
        /// The caller owns the returned handle and must release it with Destroy.
        /// </summary>
        public static IntPtr Create(bool active)
        {
            var width = Math.Max(16, GetSystemMetrics(SmCxSmallIcon));
            var height = Math.Max(16, GetSystemMetrics(SmCySmallIcon));

            var bitmap = DrawBitmap(
                width,
                height,
                active ? ActiveColor : InactiveColor);

            return CreateIconHandle(bitmap);
        }

        /// <summary>
        /// Releases one HICON returned by Create.
        /// </summary>
        public static void Destroy(IntPtr iconHandle)
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
        }

        /// <summary>
        /// Draws the MaxLoud pressure-gauge glyph into a transparent WPF bitmap.
        /// </summary>
        private static RenderTargetBitmap DrawBitmap(
            int width,
            int height,
            Color color)
        {
            var visual = new DrawingVisual();

            using (var drawing = visual.RenderOpen())
            {
                DrawManometer(
                    drawing,
                    width,
                    height,
                    color);
            }

            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96.0,
                96.0,
                PixelFormats.Pbgra32);

            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>
        /// Draws the gauge outline, scale marks, needle, hub and lower stem.
        /// </summary>
        private static void DrawManometer(
            DrawingContext drawing,
            int width,
            int height,
            Color color)
        {
            var size = Math.Min(width, height);
            var scale = size / 16.0;
            var offsetX = (width - size) / 2.0;
            var offsetY = (height - size) / 2.0;

            var brush = new SolidColorBrush(color);
            brush.Freeze();

            var outlinePen = CreatePen(
                brush,
                1.35 * scale);

            var detailPen = CreatePen(
                brush,
                1.05 * scale);

            var center = new Point(
                offsetX + 8.0 * scale,
                offsetY + 7.0 * scale);

            drawing.DrawEllipse(
                null,
                outlinePen,
                new Point(
                    offsetX + 8.0 * scale,
                    offsetY + 7.0 * scale),
                6.0 * scale,
                6.0 * scale);

            DrawScaleMark(
                drawing,
                detailPen,
                center,
                5.0 * scale,
                4.0 * scale,
                -150.0);

            DrawScaleMark(
                drawing,
                detailPen,
                center,
                5.0 * scale,
                4.0 * scale,
                -120.0);

            DrawScaleMark(
                drawing,
                detailPen,
                center,
                5.0 * scale,
                4.0 * scale,
                -90.0);

            DrawScaleMark(
                drawing,
                detailPen,
                center,
                5.0 * scale,
                4.0 * scale,
                -60.0);

            DrawScaleMark(
                drawing,
                detailPen,
                center,
                5.0 * scale,
                4.0 * scale,
                -30.0);

            drawing.DrawLine(
                outlinePen,
                center,
                PointOnCircle(
                    center,
                    3.7 * scale,
                    -58.0));

            drawing.DrawEllipse(
                brush,
                null,
                center,
                1.15 * scale,
                1.15 * scale);

            drawing.DrawLine(
                outlinePen,
                new Point(
                    offsetX + 6.2 * scale,
                    offsetY + 13.0 * scale),
                new Point(
                    offsetX + 6.2 * scale,
                    offsetY + 14.8 * scale));

            drawing.DrawLine(
                outlinePen,
                new Point(
                    offsetX + 9.8 * scale,
                    offsetY + 13.0 * scale),
                new Point(
                    offsetX + 9.8 * scale,
                    offsetY + 14.8 * scale));

            drawing.DrawLine(
                detailPen,
                new Point(
                    offsetX + 6.2 * scale,
                    offsetY + 13.4 * scale),
                new Point(
                    offsetX + 9.8 * scale,
                    offsetY + 13.4 * scale));
        }

        /// <summary>
        /// Creates a rounded WPF pen suitable for a small notification-area glyph.
        /// </summary>
        private static Pen CreatePen(
            Brush brush,
            double width)
        {
            var pen = new Pen(
                brush,
                Math.Max(1.0, width))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

            pen.Freeze();
            return pen;
        }

        /// <summary>
        /// Draws one radial scale mark between the requested outer and inner radii.
        /// </summary>
        private static void DrawScaleMark(
            DrawingContext drawing,
            Pen pen,
            Point center,
            double outerRadius,
            double innerRadius,
            double angleDegrees)
        {
            drawing.DrawLine(
                pen,
                PointOnCircle(
                    center,
                    outerRadius,
                    angleDegrees),
                PointOnCircle(
                    center,
                    innerRadius,
                    angleDegrees));
        }

        /// <summary>
        /// Calculates a point using screen coordinates where positive Y points downward.
        /// </summary>
        private static Point PointOnCircle(
            Point center,
            double radius,
            double angleDegrees)
        {
            var angleRadians =
                angleDegrees * Math.PI / 180.0;

            return new Point(
                center.X + radius * Math.Cos(angleRadians),
                center.Y + radius * Math.Sin(angleRadians));
        }

        /// <summary>
        /// Copies a Pbgra32 WPF bitmap to a top-down 32-bit DIB and creates a native HICON.
        /// </summary>
        private static IntPtr CreateIconHandle(
            BitmapSource bitmap)
        {
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];

            bitmap.CopyPixels(
                pixels,
                stride,
                0);

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)pixels.Length
                }
            };

            var colorBitmap = CreateDIBSection(
                IntPtr.Zero,
                ref bitmapInfo,
                DibRgbColors,
                out var bitmapBits,
                IntPtr.Zero,
                0);

            if (colorBitmap == IntPtr.Zero ||
                bitmapBits == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "CreateDIBSection failed while creating the MaxLoud tray icon.");
            }

            IntPtr maskBitmap = IntPtr.Zero;

            try
            {
                Marshal.Copy(
                    pixels,
                    0,
                    bitmapBits,
                    pixels.Length);

                maskBitmap = CreateBitmap(
                    width,
                    height,
                    1,
                    1,
                    IntPtr.Zero);

                if (maskBitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "CreateBitmap failed while creating the MaxLoud tray icon mask.");
                }

                var iconInfo = new IconInfo
                {
                    IsIcon = true,
                    XHotspot = 0,
                    YHotspot = 0,
                    MaskBitmap = maskBitmap,
                    ColorBitmap = colorBitmap
                };

                var iconHandle = CreateIconIndirect(
                    ref iconInfo);

                if (iconHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "CreateIconIndirect failed while creating the MaxLoud tray icon.");
                }

                return iconHandle;
            }
            finally
            {
                if (maskBitmap != IntPtr.Zero)
                {
                    DeleteObject(maskBitmap);
                }

                DeleteObject(colorBitmap);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ColorsUsed;
            public uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool IsIcon;

            public uint XHotspot;
            public uint YHotspot;
            public IntPtr MaskBitmap;
            public IntPtr ColorBitmap;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr deviceContext,
            ref BitmapInfo bitmapInfo,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateBitmap(
            int width,
            int height,
            uint planes,
            uint bitsPerPixel,
            IntPtr bits);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateIconIndirect(
            ref IconInfo iconInfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr iconHandle);
    }
}
