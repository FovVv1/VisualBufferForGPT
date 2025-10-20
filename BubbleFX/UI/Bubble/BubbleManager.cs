using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace VisualBuffer.BubbleFX.UI.Bubble
{
    public sealed class BubbleManager
    {
        public static BubbleManager Instance { get; } = new();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        public void Show(string text)
        {
            var w = new BubbleWindow(text);

            if (GetCursorPos(out var pt))
            {
                w.Left = pt.X + 12;
                w.Top = pt.Y + 12;
            }
            else
            {
                // запасной — по центру экрана
                w.Left = (SystemParameters.PrimaryScreenWidth - w.Width) / 2;
                w.Top = (SystemParameters.PrimaryScreenHeight - w.Height) / 2;
            }

            w.Show();
        }
    }
}
