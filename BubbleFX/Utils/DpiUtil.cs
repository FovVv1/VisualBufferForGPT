using System;
using System.Windows;
using System.Windows.Media;


namespace VisualBuffer.BubbleFX.Utils
{
    public static class DpiUtil
    {
        public static (double scaleX, double scaleY) GetDpiScale(Visual v)
        {
            var src = PresentationSource.FromVisual(v);
            if (src?.CompositionTarget is not null)
            {
                var m = src.CompositionTarget.TransformToDevice;
                return (m.M11, m.M22);
            }
            var dpi = VisualTreeHelper.GetDpi(v);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }


        public static Point ScreenToWpf(Visual v, Point screenPoint)
        {
            var p = screenPoint;
            var (sx, sy) = GetDpiScale(v);
            return new Point(p.X / sx, p.Y / sy);
        }


        public static Point WpfToScreen(Visual v, Point wpfPoint)
        {
            var (sx, sy) = GetDpiScale(v);
            return new Point(wpfPoint.X * sx, wpfPoint.Y * sy);
        }
    }
}