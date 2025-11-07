using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.BubbleFX.UI.Bubble
{
    public partial class BubbleResizeWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly Logger.Log _log = Logger.Create("ResizeWin");

        // поле класса
        private bool _exiting;

        // Высота заголовка (DIP)
        public double TitleBarHeight { get; set; } = Math.Max(30, SystemParameters.CaptionHeight);

        public string WindowTitle
        {
            get => _windowTitle;
            set { _windowTitle = value; PropertyChanged?.Invoke(this, new(nameof(WindowTitle))); }
        }
        private string _windowTitle = "Bubble";

        // Колбэк для выхода из режима ресайза
        private Action? _exitToContent;

        // Флаг: пользователь нажал «Показать контент»
        public bool ExitByShowContent { get; private set; }

        public BubbleResizeWindow()
        {
            InitializeComponent();
            DataContext = this;

            _log.I("Ctor: created");

            Loaded += (_, __) => _log.D($"Loaded: Size={ActualWidth:0}x{ActualHeight:0}, TopLeft=({Left:0},{Top:0}), WS={WindowState}");
            ContentRendered += (_, __) => _log.D("ContentRendered");
            StateChanged += (_, __) => _log.D($"StateChanged: {WindowState}");
            LocationChanged += (_, __) => _log.D($"LocationChanged: TopLeft=({Left:0},{Top:0})");
            SizeChanged += (_, __) => { _log.D($"SizeChanged: {ActualWidth:0}x{ActualHeight:0}"); ApplyRoundRegion(); };

            Closed += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                _log.I($"Closed: ExitByShowContent={ExitByShowContent}, hwnd=0x{hwnd.ToInt64():X}");
            };

            SourceInitialized += OnSourceInitialized;
        }

        public void Init(string title, Action onExitShowContent)
        {
            WindowTitle = string.IsNullOrWhiteSpace(title) ? "Bubble" : title;
            _exitToContent = onExitShowContent;
            _log.I($"Init: Title='{WindowTitle}', callback={(onExitShowContent != null ? "OK" : "null")}");
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var src = (HwndSource)PresentationSource.FromVisual(this)!;
            src.AddHook(WndProc);

            var hwnd = new WindowInteropHelper(this).Handle;
            var dpi = VisualTreeHelper.GetDpi(this);
            _log.I($"SourceInitialized: hwnd=0x{hwnd.ToInt64():X}, DPI=({dpi.PixelsPerDip:0.###}, {dpi.DpiScaleX:0.###}x{dpi.DpiScaleY:0.###})");

            ApplyRoundRegion();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DPICHANGED = 0x02E0;

            if (msg == WM_DPICHANGED)
            {
                _log.D("WndProc: WM_DPICHANGED");
                ApplyRoundRegion();
            }
            return IntPtr.Zero;
        }

        // ————— Верхние кнопки —————
        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            var prev = WindowState;
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            _log.I($"MaximizeClick: {prev} -> {WindowState}");
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            ExitByShowContent = false;
            _log.I("CloseClick: user requested close (NO content restore)");
            Close();
        }

        private void OnShowContentClick(object sender, RoutedEventArgs e)
        {
            if (_exiting) return;
            _exiting = true;

            Logger.Info("ResizeWin | ShowContentClick: invoking exit-to-content callback...");
            try
            {
                ExitByShowContent = true;
                _exitToContent?.Invoke();
                // дальше BubbleView сам закроет это окно
            }
            catch (Exception ex)
            {
                Logger.Error("ResizeWin | ShowContentClick: callback threw", ex);
                _exiting = false; // даём нажать ещё раз, если что-то пошло не так
            }

            ExitByShowContent = true;
            _log.I("ShowContentClick: invoking exit-to-content callback...");
            try
            {
                if (_exitToContent == null)
                {
                    _log.W("ShowContentClick: callback is null! Nothing to do.");
                    return;
                }

                _exitToContent.Invoke();
                _log.I("ShowContentClick: callback returned; waiting for host to close me.");
                // ВАЖНО: окно закрывается из BubbleView после возврата контента
            }
            catch (Exception ex)
            {
                _log.E("ShowContentClick: callback threw", ex);
            }
        }

        // Физическое скругление (опционально)
        private void ApplyRoundRegion()
        {
            try
            {
                if (!IsLoaded) { _log.D("ApplyRoundRegion: skipped (not loaded)"); return; }

                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) { _log.W("ApplyRoundRegion: hwnd=0"); return; }

                var dpi = VisualTreeHelper.GetDpi(this);
                int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
                int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
                int r = (int)Math.Round(12 * dpi.DpiScaleX);

                _log.D($"ApplyRoundRegion: w={w}, h={h}, r={r}");

                IntPtr hrgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
                SetWindowRgn(hwnd, hrgn, true);
            }
            catch (Exception ex)
            {
                _log.E("ApplyRoundRegion failed", ex);
            }
        }

        // P/Invoke
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    }
}
