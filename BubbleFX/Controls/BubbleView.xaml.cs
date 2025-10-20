using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;

using VisualBuffer.BubbleFX.Models;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Insert;   // InsertEngine
using VisualBuffer.BubbleFX.Utils;       // DpiUtil
using VisualBuffer.Diagnostics;          // Log
using VisualBuffer.Services;             // DragHelper

namespace VisualBuffer.BubbleFX.Controls
{
    public partial class BubbleView : UserControl
    {
        public BubbleViewModel VM => (BubbleViewModel)DataContext;

        private readonly DragHelper _drag = new();
        private BubbleWindow? _floatWindow;
        private BubbleHostCanvas? _host;
        private Point _localGrab;

        // hover / arm
        private readonly DispatcherTimer _hoverWatch;
        private Point _lastScreen;
        private DateTime _lastMoveAtUtc;
        private bool _hoverArmed;

        private const int HoverMs = 1000;
        private const double MoveSlopPx = 3.0;

        public BubbleView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            _hoverWatch = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _hoverWatch.Tick += HoverWatch_Tick;
        }

        private void OnLoaded(object? s, RoutedEventArgs e)
        {
            _host = FindAncestorHost();

            if (_host is not null)
            {
                Canvas.SetLeft(this, VM.X);
                Canvas.SetTop(this, VM.Y);
                Width = VM.Width;
                Height = VM.Height;
            }
            else
            {
                _floatWindow = Window.GetWindow(this) as BubbleWindow;
                Width = VM.Width;
                Height = VM.Height;
                VM.IsFloating = true;
            }

            // drag: down — на Header; move/up — на всём контроле
            Header.MouseLeftButtonDown += Header_MouseLeftButtonDown;
            PreviewMouseMove += Root_MouseMove;
            PreviewMouseLeftButtonUp += Root_MouseLeftButtonUp;

            if (CloseBtn != null)
                CloseBtn.Click += (_, __) => RequestClose();
        }

        private void OnUnloaded(object? s, RoutedEventArgs e)
        {
            Header.MouseLeftButtonDown -= Header_MouseLeftButtonDown;
            PreviewMouseMove -= Root_MouseMove;
            PreviewMouseLeftButtonUp -= Root_MouseLeftButtonUp;

            _hoverWatch.Stop();
            _floatWindow = null;
        }

        private BubbleHostCanvas? FindAncestorHost()
        {
            DependencyObject p = this;
            while (p is not null)
            {
                if (p is BubbleHostCanvas c) return c;
                p = VisualTreeHelper.GetParent(p);
            }
            return null;
        }

        // ======== Drag lifecycle ========

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // не стартуем drag, если начало — с кнопки закрытия
            if (IsWithin((DependencyObject)e.OriginalSource, CloseBtn))
                return;

            Focus();
            CaptureMouse();

            var local = e.GetPosition(this);
            var screen = PointToScreen(local);

            _localGrab = local;
            _drag.OnDown(local, screen, DateTime.UtcNow);

            ArmReset();
            _lastScreen = screen;
            _lastMoveAtUtc = DateTime.UtcNow;
            _hoverWatch.Start();
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drag.IsDown) return;

            var local = e.GetPosition(this);
            var screen = PointToScreen(local);

            _drag.MaybeBeginDrag(screen);
            _drag.OnMove(screen);

            if (!_drag.IsDragging) return;

            if (Distance2(screen, _lastScreen) > MoveSlopPx * MoveSlopPx)
            {
                _lastScreen = screen;
                _lastMoveAtUtc = DateTime.UtcNow;
                ArmReset();
            }

            if (VM.IsFloating)
            {
                var w = _floatWindow ?? (Window.GetWindow(this) as BubbleWindow);
                if (w is not null)
                {
                    var (sx, sy) = DpiUtil.GetDpiScale(w);
                    w.Left = screen.X / sx - _localGrab.X;
                    w.Top = screen.Y / sy - _localGrab.Y;
                }

                TryTearIn(screen);
            }
            else
            {
                if (_host is not null)
                {
                    var hostLocal = _host.ScreenToLocal(screen);
                    Canvas.SetLeft(this, hostLocal.X - _localGrab.X);
                    Canvas.SetTop(this, hostLocal.Y - _localGrab.Y);
                    TryTearOut(screen);
                }
            }
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            var wasTap = _drag.IsTapNow();
            _hoverWatch.Stop();

            if (_drag.IsDragging && !VM.IsFloating && _host is not null)
            {
                VM.X = Canvas.GetLeft(this);
                VM.Y = Canvas.GetTop(this);
            }

            // === вставка строго на отпускание, если «вооружены» ===
            if (_hoverArmed)
            {
                bool ok = TryPasteAtCursorThroughBubble(VM.ContentText ?? string.Empty);
                if (ok)
                    RequestClose();
            }

            ArmReset();
            _drag.Reset();

            if (wasTap) e.Handled = true;
        }

        // ======== Hover watch: вооружение по паузе ========

        private void HoverWatch_Tick(object? sender, EventArgs e)
        {
            if (!_drag.IsDragging) return;
            if (!VM.IsFloating) return;
            if (_hoverArmed) return;

            var idleMs = (DateTime.UtcNow - _lastMoveAtUtc).TotalMilliseconds;
            if (idleMs < HoverMs) return;

            // достаточно простой критерий: простояли ≥1s — «вооружены»
            _hoverArmed = true;
            Logger.Info("Bubble: Paste armed");
        }

        private void ArmReset() => _hoverArmed = false;

        // ======== Tear-out / Tear-in ========

        private void TryTearOut(Point screenPoint)
        {
            if (_host is null || VM.IsFloating) return;

            const double marginPx = 20;
            var local = _host.ScreenToLocal(screenPoint);
            var rect = new Rect(new Point(0, 0), _host.RenderSize);
            var safe = Rect.Inflate(rect, -marginPx, -marginPx);

            if (!safe.Contains(local))
            {
                _host.Children.Remove(this);

                _floatWindow = new BubbleWindow
                {
                    Owner = Application.Current.MainWindow,
                    Content = this,
                    Topmost = true
                };

                var (sx, sy) = DpiUtil.GetDpiScale(_floatWindow);
                _floatWindow.Left = screenPoint.X / sx - _localGrab.X;
                _floatWindow.Top = screenPoint.Y / sy - _localGrab.Y;

                _floatWindow.Show();
                VM.IsFloating = true;
            }
        }

        private void TryTearIn(Point screenPoint)
        {
            if (_host is null || !VM.IsFloating) return;

            if (_host.IsScreenPointOverHost(screenPoint))
            {
                if (_floatWindow is not null)
                {
                    _floatWindow.Content = null;
                    _floatWindow.Close();
                    _floatWindow = null;
                }

                var hostLocal = _host.ScreenToLocal(screenPoint);
                _host.AddBubble(this, new Point(hostLocal.X - _localGrab.X, hostLocal.Y - _localGrab.Y));

                VM.IsFloating = false;
                VM.X = Canvas.GetLeft(this);
                VM.Y = Canvas.GetTop(this);
                ArmReset();
            }
        }

        // ======== Закрытие ========

        private void RequestClose()
        {
            if (VM.IsFloating)
            {
                if (_floatWindow is not null)
                {
                    _floatWindow.Content = null;
                    _floatWindow.Close();
                    _floatWindow = null;
                }
            }
            else
            {
                _host?.Children.Remove(this);
            }
        }

        // ======== Ключевой момент: вызвать PasteAtCursorFocus «сквозь» пузырь ========

        private bool TryPasteAtCursorThroughBubble(string text)
        {
            try
            {
                // Если плавающий — на мгновение делаем окно прозрачным для хит-теста,
                // чтобы InsertEngine увидел «подложку» по WindowFromPoint.
                if (_floatWindow is not null)
                {
                    var hwndThis = new WindowInteropHelper(_floatWindow).Handle;
                    if (hwndThis != nint.Zero)
                    {
                        var origEx = GetWindowLongPtrSafe(hwndThis, GWL_EXSTYLE);
                        try
                        {
                            var newEx = new IntPtr(origEx.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                            SetWindowLongPtrSafe(hwndThis, GWL_EXSTYLE, newEx);

                            InsertEngine.Instance.PasteAtCursorFocus(text);
                        }
                        finally
                        {
                            SetWindowLongPtrSafe(hwndThis, GWL_EXSTYLE, origEx);
                        }
                    }
                    else
                    {
                        InsertEngine.Instance.PasteAtCursorFocus(text);
                    }
                }
                else
                {
                    // На холсте — и так не topmost
                    InsertEngine.Instance.PasteAtCursorFocus(text);
                }

                return true; // считаем попытку успешной — InsertEngine делает фолбэки сам
            }
            catch (Exception ex)
            {
                Logger.Error("Bubble: PasteAtCursorThroughBubble failed.", ex);
                return false;
            }
        }

        // ======== Утилиты ========

        private static bool IsWithin(DependencyObject? src, FrameworkElement? target)
        {
            if (src is null || target is null) return false;
            for (var d = src; d != null; d = VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, target)) return true;
            return false;
        }

        private static double Distance2(Point a, Point b)
        {
            var dx = a.X - b.X; var dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        // ======== Win32 ========

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);

        private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
