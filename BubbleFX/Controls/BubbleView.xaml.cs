using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VisualBuffer.BubbleFX.Models;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Canvas;   // <-- ДОБАВИЛИ доступ к CanvasWindow
using VisualBuffer.BubbleFX.UI.Insert;   // InsertEngine
using VisualBuffer.BubbleFX.Utils;       // DpiUtil
using VisualBuffer.Diagnostics;          // Logger

namespace VisualBuffer.BubbleFX.Controls
{
    public partial class BubbleView : UserControl
    {
        public BubbleViewModel VM => (BubbleViewModel)DataContext;

        private readonly Services.DragHelper _drag = new();
        private BubbleWindow? _floatWindow;
        private BubbleHostCanvas? _host;
        private Point _localGrab;

        private BubbleHostCanvas? _dockCandidate;
        private Point _dockCandidateLocal;
        private bool _tearOutCandidate;
        private Point _tearOutScreen;

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
                VM.IsFloating = false;
            }
            else
            {
                _floatWindow = Window.GetWindow(this) as BubbleWindow;
                Width = VM.Width;
                Height = VM.Height;
                VM.IsFloating = true;
            }

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

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
                // двигать окно
                var w = _floatWindow ?? (Window.GetWindow(this) as BubbleWindow);
                if (w is not null)
                {
                    var (sx, sy) = DpiUtil.GetDpiScale(w);
                    w.Left = screen.X / sx - _localGrab.X;
                    w.Top  = screen.Y / sy - _localGrab.Y;
                }

                // выбрать КАНДИДАТ на док
                _dockCandidate = null;
                var host = CanvasWindow.Instance?.HostElement;
                if (host != null && BubbleHostCanvas.IsUsable(host) && host.IsScreenPointOverHost(screen))
                {
                    var pLocal = host.ScreenToLocal(screen);
                    _dockCandidate = host;
                    _dockCandidateLocal = new Point(pLocal.X - _localGrab.X, pLocal.Y - _localGrab.Y);
                    Logger.Info("Bubble: dock-candidate found");
                }
            }
            else
            {
                if (_host is not null)
                {
                    // двигаем пузырь в хосте
                    var hostLocalAtCursor = _host.ScreenToLocal(screen);
                    var newTopLeft = new Point(hostLocalAtCursor.X - _localGrab.X,
                                               hostLocalAtCursor.Y - _localGrab.Y);
                    Canvas.SetLeft(this, newTopLeft.X);
                    Canvas.SetTop(this,  newTopLeft.Y);

                    // кандидат на отстыковку: рамка пузыря вышла за «safe»
                    const double marginPx = 20;
                    var hostRect = new Rect(new Point(0, 0), _host.RenderSize);
                    var safe = Rect.Inflate(hostRect, -marginPx, -marginPx);
                    var bubbleRect = new Rect(newTopLeft, new Size(ActualWidth, ActualHeight));

                    if (!safe.Contains(bubbleRect))
                    {
                        _tearOutCandidate = true;
                        _tearOutScreen = _host.PointToScreen(newTopLeft);
                    }
                    else
                    {
                        _tearOutCandidate = false;
                    }
                }
            }
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            var wasTap = _drag.IsTapNow();
            _hoverWatch.Stop();

            // финализируем reparent
            if (VM.IsFloating)
            {
                if (_dockCandidate != null)
                {
                    if (_floatWindow is not null)
                    {
                        _floatWindow.Content = null;
                        _floatWindow.Close();
                        _floatWindow = null;
                    }

                    _dockCandidate.AddBubble(this, _dockCandidateLocal);
                    _host = _dockCandidate;
                    VM.IsFloating = false;
                    Logger.Info("Bubble: docked into host");
                }
            }
            else
            {
                if (_tearOutCandidate)
                {
                    _host?.Children.Remove(this);
                    _host = null;

                    _floatWindow = new BubbleWindow
                    {
                        Owner = Application.Current.MainWindow,
                        Content = this,
                        Topmost = true
                    };

                    var (sx, sy) = DpiUtil.GetDpiScale(_floatWindow);
                    _floatWindow.Left = _tearOutScreen.X / sx;
                    _floatWindow.Top  = _tearOutScreen.Y / sy;
                    _floatWindow.Show();

                    VM.IsFloating = true;
                    Logger.Info("Bubble: undocked to window");
                }
                else if (_drag.IsDragging && _host is not null)
                {
                    VM.X = Canvas.GetLeft(this);
                    VM.Y = Canvas.GetTop(this);
                }
            }

            _dockCandidate = null;
            _tearOutCandidate = false;

            if (_hoverArmed)
            {
                bool ok = TryPasteAtCursorThroughBubble(VM.ContentText ?? string.Empty);
                if (ok) RequestClose();
            }

            ArmReset();
            _drag.Reset();

            if (wasTap) e.Handled = true;
        }

        private bool TryPasteAtCursorThroughBubble(string text)
        {
            try
            {
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
                            System.Threading.Thread.Sleep(1);

                            // диагностический лог: кто под курсором
                            if (GetCursorPos(out POINT pt))
                            {
                                var h = WindowFromPoint(pt);
                                var top = GetAncestor(h, GA_ROOT);
                                Logger.Info($"Bubble: UnderCursor hwnd=0x{h.ToInt64():X} top=0x{top.ToInt64():X} clsTop='{ClassOf(top)}' clsPoint='{ClassOf(h)}'");
                            }

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
                    InsertEngine.Instance.PasteAtCursorFocus(text);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Bubble: PasteAtCursorThroughBubble failed.", ex);
                return false;
            }
        }

        private void HoverWatch_Tick(object? sender, EventArgs e)
        {
            if (!_drag.IsDragging) return;
            if (!VM.IsFloating) return;
            if (_hoverArmed) return;

            var idleMs = (DateTime.UtcNow - _lastMoveAtUtc).TotalMilliseconds;
            if (idleMs < HoverMs) return;

            _hoverArmed = true;
            Logger.Info("Bubble: Paste armed");
        }

        private void ArmReset() => _hoverArmed = false;

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



        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int cap);

        private static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            return GetClassName(h, sb, sb.Capacity) != 0 ? sb.ToString() : "";
        }

        private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

        private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const uint GA_ROOT = 2;
    }
}

