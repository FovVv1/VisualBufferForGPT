using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisualBuffer.BubbleFX.Models;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Canvas;   // CanvasWindow, BubbleHostCanvas
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

        // кандидаты док/андок
        private BubbleHostCanvas? _dockCandidate;
        private Point _dockCandidateLocal;

        // hover-paste
        private readonly DispatcherTimer _hoverWatch;
        private Point _lastScreen;
        private DateTime _lastMoveAtUtc;
        private bool _hoverArmed;

        // lock (pin)
        private bool _isLocked = false;

        // редактирование заголовка
        private bool _isTitleEditing = false;
        private string _titleBeforeEdit = "";

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

            // drag по всему пузырю
            Root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
            PreviewMouseMove += Root_MouseMove;
            PreviewMouseLeftButtonUp += Root_MouseLeftButtonUp;

            UpdateLockVisual();
        }

        private void OnUnloaded(object? s, RoutedEventArgs e)
        {
            Root.MouseLeftButtonDown -= Root_MouseLeftButtonDown;
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

        // ======== PIN ========

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            _isLocked = !_isLocked;
            UpdateLockVisual();
        }

        private void UpdateLockVisual()
        {
            var uri = _isLocked
                ? new Uri("pack://application:,,,/Resources/redPin3.png")
                : new Uri("pack://application:,,,/Resources/transparentPin.png");
            PinIcon.Source = new BitmapImage(uri);

            // блокируем хедер/контент/крестик; пин — всегда кликабелен
            HeaderTitleArea.IsHitTestVisible = !_isLocked;
            CloseArea.IsHitTestVisible = !_isLocked;
            CloseBtn.IsEnabled = !_isLocked;
            ContentArea.IsHitTestVisible = !_isLocked;

            double dim = _isLocked ? 0.9 : 1.0;
            ContentArea.Opacity = dim;
            HeaderTitleArea.Opacity = dim;
            CloseArea.Opacity = dim;

            // если залочили — гарантированно выходим из режима редактирования
            if (_isLocked && _isTitleEditing) CancelTitleEdit();
        }

        // ======== Drag lifecycle ========

        private void Root_MouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            if (_isLocked) return;

            var src = e.OriginalSource as DependencyObject;

            // не стартуем drag, если клик по пину / кресту / полю редактирования
            if (IsWithin(src, PinBtn) || IsWithin(src, CloseBtn) || IsWithin(src, TitleEdit))
                return;

            // не стартуем drag, если это даблклик по заголовку (переключаем в edit)
            if (IsWithin(src, HeaderTitleArea) && e is { ClickCount: 2 })
            {
                BeginTitleEdit();
                e.Handled = true;
                return;
            }

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

        private void Root_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_isLocked || _isTitleEditing) return;
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
                // двигаем окно
                var w = _floatWindow ?? (Window.GetWindow(this) as BubbleWindow);
                if (w is not null)
                {
                    var (sx, sy) = DpiUtil.GetDpiScale(w);
                    w.Left = screen.X / sx - _localGrab.X;
                    w.Top = screen.Y / sy - _localGrab.Y;
                }

                // выбираем КАНДИДАТ на док (но не докаем сейчас)
                _dockCandidate = null;
                var host = CanvasWindow.Instance?.HostElement;
                if (host != null && BubbleHostCanvas.IsUsable(host) && host.IsScreenPointOverHost(screen))
                {
                    var pLocal = host.ScreenToLocal(screen);
                    _dockCandidate = host;
                    _dockCandidateLocal = new Point(pLocal.X - _localGrab.X, pLocal.Y - _localGrab.Y);
                }
            }
            else
            {
                if (_host is not null)
                {
                    // двигаем внутри холста
                    var hostLocalAtCursor = _host.ScreenToLocal(screen);
                    var newTopLeft = new Point(hostLocalAtCursor.X - _localGrab.X,
                                               hostLocalAtCursor.Y - _localGrab.Y);
                    Canvas.SetLeft(this, newTopLeft.X);
                    Canvas.SetTop(this, newTopLeft.Y);

                    // немедленный ан-док при выходе за safe-рамку
                    const double marginPx = 20;
                    var hostRect = new Rect(new Point(0, 0), _host.RenderSize);
                    var safe = Rect.Inflate(hostRect, -marginPx, -marginPx);
                    var bubbleRect = new Rect(newTopLeft, new Size(ActualWidth, ActualHeight));

                    if (!safe.Contains(bubbleRect))
                    {
                        _host.Children.Remove(this);
                        var screenTL = _host.PointToScreen(newTopLeft);
                        _host = null;

                        _floatWindow = new BubbleWindow
                        {
                            Owner = Application.Current.MainWindow,
                            Content = this,
                            Topmost = true
                        };

                        var (sx, sy) = DpiUtil.GetDpiScale(_floatWindow);
                        _floatWindow.Left = screenTL.X / sx;
                        _floatWindow.Top = screenTL.Y / sy;
                        _floatWindow.Show();
                        VM.IsFloating = true;

                        if (!IsMouseCaptured) CaptureMouse();
                    }
                }
            }
        }

        private void Root_MouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            var wasTap = _drag.IsTapNow();
            _hoverWatch.Stop();

            // финал дока
            if (VM.IsFloating)
            {
                if (_dockCandidate != null)
                {
                    var host = _dockCandidate;
                    var pLocal = host.ScreenToLocal(_lastScreen);
                    var targetTL = new Point(pLocal.X - _localGrab.X, pLocal.Y - _localGrab.Y);

                    if (_floatWindow is not null)
                    {
                        _floatWindow.Content = null;
                        _floatWindow.Close();
                        _floatWindow = null;
                    }

                    host.AddBubble(this, targetTL);
                    host.UpdateLayout();

                    Canvas.SetLeft(this, targetTL.X);
                    Canvas.SetTop(this, targetTL.Y);
                    VM.X = targetTL.X;
                    VM.Y = targetTL.Y;

                    _host = host;
                    VM.IsFloating = false;
                    Panel.SetZIndex(this, short.MaxValue - 1);
                }
            }
            else
            {
                if (_drag.IsDragging && _host is not null)
                {
                    VM.X = Canvas.GetLeft(this);
                    VM.Y = Canvas.GetTop(this);
                }
            }

            _dockCandidate = null;

            // вставка по arm
            if (_hoverArmed)
            {
                bool ok = TryPasteAtCursorThroughBubble(VM.ContentText ?? string.Empty);
                if (ok) RequestClose();
            }

            ArmReset();
            _drag.Reset();
            if (wasTap) e.Handled = true;
        }

        // ======== редактирование заголовка ========

        private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isLocked) return;
            if (e.ClickCount == 2)
            {
                BeginTitleEdit();
                e.Handled = true; // чтобы даблклик не стартовал drag
            }
        }

        private void BeginTitleEdit()
        {
            if (_isTitleEditing) return;
            _isTitleEditing = true;
            _titleBeforeEdit = VM?.Title ?? "";

            TitleText.Visibility = Visibility.Collapsed;
            TitleEdit.Visibility = Visibility.Visible;

            TitleEdit.Focus();
            TitleEdit.SelectAll();
        }

        private void CommitTitleEdit()
        {
            _isTitleEditing = false;
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }

        private void CancelTitleEdit()
        {
            if (VM != null) VM.Title = _titleBeforeEdit;
            _isTitleEditing = false;
            TitleEdit.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }

        private void TitleEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTitleEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTitleEdit();
                e.Handled = true;
            }
        }

        private void TitleEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_isTitleEditing) CommitTitleEdit();
        }

        // ======== Hover-paste ========

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

        // ======== Вставка через InsertEngine «сквозь» пузырь ========

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

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => RequestClose();

        // ======== Win32 ========

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint GA_ROOT = 2;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int cap);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
        private static IntPtr SetWindowLongPtrSafe(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
            => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

        private static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            return GetClassName(h, sb, sb.Capacity) != 0 ? sb.ToString() : "";
        }
    }
}
