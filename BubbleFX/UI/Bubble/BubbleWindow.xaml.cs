using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows;                   // ContentOperations
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media;             // VisualTreeHelper, Visual
using System.Windows.Media.Media3D;
using System.Windows.Media.Media3D;     // Visual3D
using System.Windows.Threading;
using VisualBuffer.BubbleFX.Controls;
using VisualBuffer.BubbleFX.Models;

namespace VisualBuffer.BubbleFX.UI.Bubble
{
    public partial class BubbleWindow : Window
    {
        private HwndSource? _src;
        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _origExStyle = IntPtr.Zero;
        private bool _mouseTransparent;
        private DispatcherTimer? _pinHoverPoll;
         
        public BubbleWindow()
        {
            InitializeComponent();
        }

        // НОВАЯ перегрузка: позволяет передать текст
        public BubbleWindow(string text) : this()
        {
            var vm = new BubbleViewModel
            {
                Title = "Bubble",
                Width = 300,
                Height = 180,
                IsFloating = true,
                X = 0,
                Y = 0,
                ContentText = text,   // <- см. п.3
            };

            var view = new BubbleView { DataContext = vm };
            Content = view;
        }

        public bool ClickThroughWhenPinned { get; set; }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _src = (HwndSource)PresentationSource.FromVisual(this)!;
            _src.AddHook(WndProc);
            _hwnd = new WindowInteropHelper(this).Handle;
            _origExStyle = GetWindowLongPtrSafe(_hwnd, GWL_EXSTYLE);

            // Не крадём фокус “по жизни” (клик по PIN всё равно работает)
            SetWindowLongPtrSafe(_hwnd, GWL_EXSTYLE,
                new IntPtr(_origExStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

            _pinHoverPoll = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _pinHoverPoll.Tick += (_, __) =>
            {
                if (!IsLoaded || _hwnd == IntPtr.Zero) return;

                if (ClickThroughWhenPinned)
                {
                    // делаем окно сквозным, кроме зоны PIN
                    bool overPin = IsCursorOverPin();
                    SetMouseTransparent(!overPin);
                }
                else
                {
                    // вне PIN-режима — на всякий случай возвращаем обычный стиль
                    if (_mouseTransparent) SetMouseTransparent(false);
                }
            };
            _pinHoverPoll.Start();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (ClickThroughWhenPinned) // активируем только в режиме PIN + float
            {
                // 1) Пока курсор НЕ над PIN — делаем окно “сквозным” железно.
                if (msg == WM_MOUSEMOVE || msg == WM_NCHITTEST)
                {
                    bool overPin = IsCursorOverPin();
                    SetMouseTransparent(!overPin);

                    if (!overPin && msg == WM_NCHITTEST)
                    {
                        handled = true;
                        return (IntPtr)HTTRANSPARENT; // отдаём событие окну снизу
                    }
                }

                // 2) Не активируемся кликом сквозь окно
                if (msg == WM_MOUSEACTIVATE)
                {
                    handled = true;
                    return (IntPtr)MA_NOACTIVATE; // не красть фокус у нижнего окна
                }
            }

            return IntPtr.Zero;
        }


        private static bool IsDescendantOf(DependencyObject node, DependencyObject root)
        {
            for (var d = node; d != null; d = ParentOf(d))
                if (ReferenceEquals(d, root)) return true;
            return false;
        }
        private static DependencyObject? ParentOf(DependencyObject? d)
        {
            if (d is null) return null;

            // Визуальное дерево
            if (d is Visual || d is Visual3D)
                return VisualTreeHelper.GetParent(d);

            // Текстовые/документные элементы (Run/Inline/Paragraph/…)
            if (d is FrameworkContentElement fce)
                return fce.Parent; // доступен у FCE (TextElement/Inline и т.п.)

            if (d is ContentElement ce)
                return ContentOperations.GetParent(ce); // для базового ContentElement

            // Фоллбэк — логическое дерево
            return LogicalTreeHelper.GetParent(d);
        }
        // Тогглер WS_EX_TRANSPARENT
        private void SetMouseTransparent(bool on)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_mouseTransparent == on) return;

            var ex = GetWindowLongPtrSafe(_hwnd, GWL_EXSTYLE).ToInt64();
            if (on) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLongPtrSafe(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
            _mouseTransparent = on;
        }


        // Проверка “курсор над PIN?”
        private bool IsCursorOverPin()
        {
            if (Content is not BubbleView view) return false;
            if (!GetCursorPos(out var pt)) return false;
            var pInView = view.PointFromScreen(new Point(pt.X, pt.Y));
            var hit = view.InputHitTest(pInView) as DependencyObject;
            return hit != null && IsDescendantOf(hit, view.PinClickTarget);
        }

        // --- Win32 константы/импорт (внизу файла, можно оставить твои):
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_MOUSEACTIVATE = 0x0021;

        private const int HTTRANSPARENT = -1;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int MA_NOACTIVATE = 3;
        private const int MA_NOACTIVATEANDEAT = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

        // ===== Win32 interop (x86/x64-safe) =====
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8
               ? GetWindowLongPtr64(hWnd, nIndex)
               : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            => IntPtr.Size == 8
               ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
               : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    }
}
