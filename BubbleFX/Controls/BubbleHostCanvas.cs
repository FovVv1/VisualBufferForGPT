using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisualBuffer.BubbleFX.Utils;

namespace VisualBuffer.BubbleFX.Controls
{
    public class BubbleHostCanvas : Canvas
    {
        public static bool IsUsable(Visual v) => PresentationSource.FromVisual(v) != null;

        public bool IsScreenPointOverHost(Point screenPoint)
        {
            if (!IsUsable(this)) return false;
            var p = PointFromScreen(DpiUtil.ScreenToWpf(this, screenPoint));
            var rect = new Rect(new Point(0, 0), RenderSize);
            return rect.Contains(p);
        }

        public Point ScreenToLocal(Point screenPoint)
        {
            if (!IsUsable(this)) return new Point(-1, -1);
            return PointFromScreen(DpiUtil.ScreenToWpf(this, screenPoint));
        }

        public void AddBubble(UIElement bubble, Point localTopLeft)
        {
            if (!Children.Contains(bubble)) Children.Add(bubble);
            SetLeft(bubble, localTopLeft.X);
            SetTop(bubble, localTopLeft.Y);
        }
    }
}
