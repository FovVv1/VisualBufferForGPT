using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisualBuffer.BubbleFX.Utils;


namespace VisualBuffer.BubbleFX.Controls
{
    /// <summary>
    /// Canvas hosting docked (non-floating) bubbles. Provides helpers for tear-out/tear-in.
    /// </summary>
    public class BubbleHostCanvas : Canvas
    {
        public bool IsScreenPointOverHost(Point screenPoint)
        {
            var p = PointFromScreen(DpiUtil.ScreenToWpf(this, screenPoint));
            var rect = new Rect(new Point(0, 0), RenderSize);
            return rect.Contains(p);
        }


        public Point ScreenToLocal(Point screenPoint)
        {
            var local = PointFromScreen(DpiUtil.ScreenToWpf(this, screenPoint));
            return local;
        }


        public void AddBubble(UIElement bubble, Point localTopLeft)
        {
            if (!Children.Contains(bubble)) Children.Add(bubble);
            SetLeft(bubble, localTopLeft.X);
            SetTop(bubble, localTopLeft.Y);
        }
    }
}