using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace VisualBuffer.BubbleFX.UI.Bubble
{
    public sealed class BubbleResizeWindow : Window
    {
        private HwndSource? _src;

        public event EventHandler? ResizeMoveFinished;

        private const double CornerR = 14;                 // желаемый радиус в DIP
        private static readonly Thickness Grip = new(14);  // зона захвата для ресайза

        public BubbleResizeWindow()
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;

            // Рамка для ресайза от ОС
            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = Grip,
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(this, chrome);

            // Визуально подрежем контент (для совпадения внутреннего рисунка)
            SizeChanged += (_, __) =>
            {
                Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight),
                                             CornerR, CornerR);
                ApplyRoundRegion(); // ← физическая форма окна
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _src = (HwndSource)PresentationSource.FromVisual(this)!;
            _src.AddHook(WndProc);

            var hwnd = new WindowInteropHelper(this).Handle;

            // Win11: отключим системные углы, чтобы не конфликтовали с нашим регионом
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int doNotRound = 1; // 1 = DoNotRound
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));

            // не светиться в Alt+Tab и не забирать фокус кликами
            const int GWL_EXSTYLE = -20;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            // применим регион сразу
            ApplyRoundRegion();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_EXITSIZEMOVE = 0x0232;
            const int WM_DPICHANGED = 0x02E0;

            if (msg == WM_EXITSIZEMOVE)
                ResizeMoveFinished?.Invoke(this, EventArgs.Empty);

            if (msg == WM_DPICHANGED)
                ApplyRoundRegion(); // DPI сменился — пересчитать регион

            return IntPtr.Zero;
        }

        // === ФИЗИЧЕСКОЕ СКРУГЛЕНИЕ ОКНА ЧЕРЕЗ REGION ===
        private void ApplyRoundRegion()
        {
            if (!IsLoaded) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // DIP → device pixels
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
            int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
            int r = Math.Max(0, (int)Math.Round(CornerR * dpi.DpiScaleX));

            // HRGN делаем чуть шире/выше (w+1/h+1), иначе может "съедать" крайний пиксель
            IntPtr hrgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);

            // После SetWindowRgn владение регионом переходит системе — DeleteObject делать НЕ нужно
            SetWindowRgn(hwnd, hrgn, true);
        }

        // P/Invoke
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
