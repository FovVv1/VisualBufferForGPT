using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using VisualBuffer.BubbleFX.Models;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Canvas;
using VisualBuffer.BubbleFX.UI.Insert;
using VisualBuffer.BubbleFX.Utils;
using VisualBuffer.Diagnostics;
using System.Windows.Media.Animation;
using System.Windows.Media;


namespace VisualBuffer.BubbleFX.Controls
{
    public partial class BubbleView : UserControl
    {

        private SolidColorBrush? _titleBrush;
        private SolidColorBrush? _contentBrush;
        public FrameworkElement PinClickTarget => PinBtn;


        private static readonly Regex Rx = new(
            @"(?<url>https?://\S+)" +
            @"|(?<comment>//.*?$)" +
            @"|(?<str>""(?:\\.|[^""])*"")" +
            @"|(?<num>\b\d+(\.\d+)?\b)" +
            @"|(?<kw>\b(class|public|private|protected|internal|static|void|int|string|var|new|return|if|else|for|foreach|while|switch|case|true|false|null)\b)" +
            @"|(?<at>@\w+)" +
            @"|(?<hash>#\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private void RenderColored(string text)
        {
            // 1) Нормализуем \r\n -> \n, чтобы не тянуть '\r' в Run'ы
            var display = (text ?? string.Empty).Replace("\r\n", "\n");

            // 2) Запрещаем перенос между '/' и '>' с помощью "word joiner" U+2060
            //    Это влияет только на РЕНДЕР, в исходных данных ничего не меняем.
            display = display.Replace("/>", "/\u2060>");

            ContentTextBlock.Inlines.Clear();
            int i = 0;

            foreach (Match m in Rx.Matches(display))
            {
                if (m.Index > i) AddPlain(display.Substring(i, m.Index - i));

                Inline colored =
                    m.Groups["url"].Success ? MakeLink(m.Value) :
                    m.Groups["comment"].Success ? MakeRun(m.Value, "#6A9955") :
                    m.Groups["str"].Success ? MakeRun(m.Value, "#CE9178") :
                    m.Groups["kw"].Success ? MakeRun(m.Value, "#569CD6", FontWeights.SemiBold) :
                    m.Groups["num"].Success ? MakeRun(m.Value, "#B5CEA8") :
                    m.Groups["at"].Success ? MakeRun(m.Value, "#D7BA7D") :
                    m.Groups["hash"].Success ? MakeRun(m.Value, "#4EC9B0") :
                    new Run(m.Value);

                ContentTextBlock.Inlines.Add(colored);
                i = m.Index + m.Length;
            }
            if (i < display.Length) AddPlain(display.Substring(i));
        }
        private static void AnimateDouble(IAnimatable target, DependencyProperty dp, double to, int ms = 160)
        {
            if (ms <= 0)
            {
                // мгновенно: снимаем анимацию и ставим значение напрямую
                target.BeginAnimation(dp, null);
                if (target is DependencyObject d) d.SetValue(dp, to);
                return;
            }

            var dur = TimeSpan.FromMilliseconds(ms);
            var da = new DoubleAnimation(to, dur)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            target.BeginAnimation(dp, da);
        }

        private static void AnimateColor(SolidColorBrush brush, Color to, int ms = 160)
        {
            var dur = TimeSpan.FromMilliseconds(ms);
            var ca = new ColorAnimation(to, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, ca);
        }


        private Inline MakeRun(string s, string hex, FontWeight? weight = null)
        {
            return new Run(s)
            {
                Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!,
                FontWeight = weight ?? FontWeights.Normal
            };
        }

    private Inline MakeLink(string url)
    {
        var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, __) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        link.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#4FC1FF")!;
        link.TextDecorations = null;
        return link;
    }

    // добавляет обычный текст с переносами строк
    private void AddPlain(string s)
    {
        // режем по \n, не теряя пустых строк; \r заранее убран в RenderColored
        var parts = s.Split(new[] { "\n" }, StringSplitOptions.None);
        for (int n = 0; n < parts.Length; n++)
        {
            ContentTextBlock.Inlines.Add(new Run(parts[n]));
            if (n < parts.Length - 1) ContentTextBlock.Inlines.Add(new LineBreak());
        }
    }

    public BubbleViewModel VM => (BubbleViewModel)DataContext;

        public bool IsPinned
        {
            get => (bool)GetValue(IsPinnedProperty);
            set => SetValue(IsPinnedProperty, value);
        }

        public static readonly DependencyProperty IsPinnedProperty =
                DependencyProperty.Register(nameof(IsPinned), typeof(bool), typeof(BubbleView), new PropertyMetadata(false, OnIsPinnedChanged));

        private readonly Services.DragHelper _drag = new();
        private BubbleWindow? _floatWindow;
        private BubbleHostCanvas? _host;
        private Point _localGrab;

        private BubbleHostCanvas? _dockCandidate;
        private Point _dockCandidateLocal;

        private readonly DispatcherTimer _hoverWatch;
        private Point _lastScreen;
        private DateTime _lastMoveAtUtc;
        private bool _hoverArmed;

        // Title edit
        private bool _isTitleEditing = false;
        private string _titleBeforeEdit = "";

        // Hover opacity (для НЕ pinned)
        private bool _isPointerOver;
        private const double OPACITY_IDLE = 0.5;
        private const double OPACITY_HOVER = 1.0;
        private const double OPACITY_DRAG = 0.25;

        // ====== Auto-resize toggle ======
        private bool _isAutoSized;
        private double _savedMiniW, _savedMiniH;

        // При PIN остальное почти невидимо, но PinIcon виден на 100%
        private const double OPACITY_PIN_OTHERS = 0.2;

        // Запомнить «звёздочку» контента, чтобы вернуть при unpin
        private readonly GridLength _contentStar = new(1, GridUnitType.Star);

        private const int HoverMs = 1000;
        private const double MoveSlopPx = 3.0;

        public BubbleView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            _hoverWatch = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(120) };
            _hoverWatch.Tick += HoverWatch_Tick;
        }


        private static void OnIsPinnedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (BubbleView)d;
            view.ApplyPinVisualAndLayout();
            view.UpdateVisualState();
            view.UpdateOpacityState();

            view._floatWindow ??= Window.GetWindow(view) as BubbleWindow;
            if (view._floatWindow != null)
                view._floatWindow.ClickThroughWhenPinned = view.IsPinned && view.VM.IsFloating;
    }


        private void ResizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isAutoSized) RestoreMiniSize();
            else AutoResizeToContent();
        }
            
        private void RestoreMiniSize()
        {
            double w = Math.Max(MinWidth, _savedMiniW > 0 ? _savedMiniW : 300);
            double h = Math.Max(MinHeight, _savedMiniH > 0 ? _savedMiniH : 180);

            if (VM.IsFloating)
            {
                _floatWindow ??= Window.GetWindow(this) as BubbleWindow;
                if (_floatWindow != null)
                {
                    _floatWindow.Width = w;
                    _floatWindow.Height = h;
                }
            }
            else
            {
                Width = w;
                Height = h;
            }

            VM.Width = w;
            VM.Height = h;

            _isAutoSized = false;
            UpdateLayout();
        }


        private void AutoResizeToContent()
        {
            // Пусто? – просто чутка увеличим, но не ломаемся
            var text = VM?.ContentText ?? string.Empty;

            // 1) Сохраним текущий компактный размер (чтобы было куда вернуться)
            if (!_isAutoSized)
            {
                _savedMiniW = VM.Width > 0 ? VM.Width : (Width > 0 ? Width : 300);
                _savedMiniH = VM.Height > 0 ? VM.Height : (Height > 0 ? Height : 180);
            }

            // 2) Ограничения по "коробке", где живёт пузырь
            const double marginPx = 24; // безопасный отступ от краёв
            double maxBoxW, maxBoxH;

            if (VM.IsFloating)
            {
                var wa = SystemParameters.WorkArea; // рабочая область монитора
                maxBoxW = Math.Max(200, wa.Width * 0.6);   // не шире 60% экрана
                maxBoxH = Math.Max(160, wa.Height * 0.75); // не выше 75% экрана
            }
            else
            {
                // Докнут на холст
                var hostSize = _host?.RenderSize ?? new Size(1200, 800);
                maxBoxW = Math.Max(200, hostSize.Width - marginPx * 2);
                maxBoxH = Math.Max(160, hostSize.Height - marginPx * 2);
            }

            // 3) Посчитаем "хром" (рамки/отступы/хедер), который не относится к чистому тексту
            //    Всё это уже есть в визуальном дереве — берём актуальные значения.
            double headerH = HeaderRow.ActualHeight > 0 ? HeaderRow.ActualHeight : 25;

            var rootBT = Root.BorderThickness;
            var caPad = ContentArea.Padding;
            var caBT = ContentArea.BorderThickness;
            var tbMar = ContentTextBlock.Margin;

            double chromeX = rootBT.Left + rootBT.Right +
                                caBT.Left + caBT.Right +
                                caPad.Left + caPad.Right +
                                tbMar.Left + tbMar.Right;

            double chromeY = rootBT.Top + rootBT.Bottom +
                                caBT.Top + caBT.Bottom +
                                caPad.Top + caPad.Bottom +
                                tbMar.Top + tbMar.Bottom +
                                headerH;

            // 4) Сначала оценим "естественную" ширину самой длинной строки (без переноса),
            //    потом ограничим её максимумом коробки.
            var probe = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap, // при бесконечной ширине переносов не будет
                FontFamily = ContentTextBlock.FontFamily,
                FontSize = ContentTextBlock.FontSize,
                FontWeight = ContentTextBlock.FontWeight,
                FontStyle = ContentTextBlock.FontStyle,
                FontStretch = ContentTextBlock.FontStretch
            };

            // «непереносная» ширина строки
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double naturalContentW = probe.DesiredSize.Width; // ширина самой длинной строки

            // максимально допустимая ширина области для текста (без хрома)
            double maxContentW = Math.Max(120, maxBoxW - chromeX);

            // целевая ширина текста — не шире длинной строки и не шире лимита:
            double targetContentW = Math.Min(naturalContentW, maxContentW);

            // после вычисления targetContentW
            var ch = Math.Max(6.0, ContentTextBlock.FontSize * 0.55); // примерно ширина символа моношрифта
            targetContentW = Math.Min(maxContentW, targetContentW + ch);

        // 5) Посчитаем высоту при этой ширине (тут уже перенос строк работает)
        probe.Width = targetContentW;
            probe.TextWrapping = TextWrapping.Wrap;
            probe.Measure(new Size(targetContentW, double.PositiveInfinity));
            double contentH = probe.DesiredSize.Height;

            // 6) Складываем с «хромом», учитываем лимит по высоте коробки
            double targetW = Math.Max(MinWidth, targetContentW + chromeX);
            double targetH = Math.Max(MinHeight, Math.Min(maxBoxH, contentH + chromeY));

            // 7) Применяем (в окно или сам контрол), синхронизируем VM
            if (VM.IsFloating)
            {
                _floatWindow ??= Window.GetWindow(this) as BubbleWindow;
                if (_floatWindow != null)
                {
                    _floatWindow.Width = targetW;
                    _floatWindow.Height = targetH;
                }
            }
            else
            {
                Width = targetW;
                Height = targetH;

                // Если уехали за край холста — подвинем внутрь безопасной зоны
                if (_host != null)
                {
                    var hostSize = _host.RenderSize;
                    double x = Canvas.GetLeft(this);
                    double y = Canvas.GetTop(this);
                    x = Math.Min(Math.Max(marginPx, x), Math.Max(marginPx, hostSize.Width - targetW - marginPx));
                    y = Math.Min(Math.Max(marginPx, y), Math.Max(marginPx, hostSize.Height - targetH - marginPx));
                    Canvas.SetLeft(this, x);
                    Canvas.SetTop(this, y);
                    VM.X = x; VM.Y = y;
                }
            }

            VM.Width = targetW;
            VM.Height = targetH;

            _isAutoSized = true;
            UpdateLayout();
        }

        private void OnLoaded(object? s, RoutedEventArgs e)
        {
            _host = FindAncestorHost();

            ApplyPinVisualAndLayout();
            UpdateOpacityState();

            Dispatcher.BeginInvoke(() =>
            {
                if (!_isAutoSized)             // только при первом появлении
                {
                    AutoResizeToContent();     // сохранит _savedMiniW/H как текущий мини-размер
                    _isAutoSized = true;       // чтобы первая кнопка "Resize" свернула назад
                }
            }, DispatcherPriority.Loaded);

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

                // Контент должен ЗАПОЛНЯТЬ окно:
                ClearValue(WidthProperty);
                ClearValue(HeightProperty);
                HorizontalAlignment = HorizontalAlignment.Stretch;
                VerticalAlignment = VerticalAlignment.Stretch;

                // Начальный размер задаём ОКНУ, а не контролу
                if (_floatWindow != null)
                {
                    _floatWindow.Width = VM.Width;
                    _floatWindow.Height = VM.Height;
                }

                VM.IsFloating = true;

                _floatWindow ??= Window.GetWindow(this) as BubbleWindow;
                if (_floatWindow != null)
                    _floatWindow.ClickThroughWhenPinned = IsPinned && VM.IsFloating;
            }

            if (VM != null)
            {
                VM.PropertyChanged += VM_PropertyChanged;
                RenderColored(VM.ContentText ?? string.Empty);
            }

            // drag за ВСЮ поверхность
            // Стало: ловим туннелинг + даже если child уже Handled
            AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Root_MouseLeftButtonDown), handledEventsToo: true);
            AddHandler(UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(Root_MouseMove), handledEventsToo: true);
            AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(Root_MouseLeftButtonUp), handledEventsToo: true);


            // прозрачность не-PIN (hover)
            Root.MouseEnter += (_, __) => { _isPointerOver = true; UpdateVisualState(); };
            Root.MouseLeave += (_, __) => { _isPointerOver = false; UpdateVisualState(); };

            _titleBrush = (SolidColorBrush)Resources["TitleBrush"];
            _contentBrush = (SolidColorBrush)Resources["ContentBrush"];

            // выставим стартовые состояния без рывка
            UpdateVisualState(instant: true);


            ApplyPinVisualAndLayout();      // привести всё к текущему состоянию
            UpdateOpacityState();           // стартовая прозрачность
        }



        private void OnUnloaded(object? s, RoutedEventArgs e)
        {
            if (VM != null) VM.PropertyChanged -= VM_PropertyChanged;

            // Снимаем глобальные AddHandler
            RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(Root_MouseLeftButtonDown));
            RemoveHandler(UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(Root_MouseMove));
            RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(Root_MouseLeftButtonUp));

            _hoverWatch.Stop();
            _floatWindow = null;
        }

        private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BubbleViewModel.ContentText))
                Dispatcher.BeginInvoke(() => RenderColored(VM.ContentText ?? string.Empty));

            if (e.PropertyName == nameof(BubbleViewModel.Title))
                Dispatcher.BeginInvoke(() => UpdateVisualState());
        }

        private void UpdateVisualState(bool instant = false)
        {
            // логика активности: тянем/над контролом => активен, иначе "idle"
            bool active = IsPinned || _drag.IsDragging || _isPointerOver;
            double targetOpacity =
                IsPinned ? 1.0 :
                _drag.IsDragging ? OPACITY_DRAG :
                _isPointerOver ? OPACITY_HOVER :
                OPACITY_IDLE;

            int ms = instant ? 0 : 160;

            // 1) Плавная прозрачность всего пузыря
            AnimateDouble(Root, UIElement.OpacityProperty, targetOpacity, ms);

            // 2) Плавное приглушение контента (поверх общей непрозрачности)
            //    Чтобы не "кричало" на idle: слегка глушим скролл-область.
            double contentOpacity = active ? 1.0 : 0.90; // мягкое приглушение
            AnimateDouble(ContentScroll, UIElement.OpacityProperty, contentOpacity, ms);

            // 3) Цвета текста
            if (_titleBrush != null)
            {
                bool hasCustomTitle = !string.IsNullOrWhiteSpace(VM?.Title) && !string.Equals(VM.Title, "Bubble", StringComparison.Ordinal);
                // placeholder — стабильный серый; пользовательский — белый в активе и мягкий серый в idle
                var titleColor = hasCustomTitle
                    ? (active ? Colors.White : Color.FromRgb(0xC8, 0xC8, 0xC8))  // белый -> светло-серый
                    : Color.FromRgb(0x9A, 0xA0, 0xA6);                           // placeholder-серый

                AnimateColor(_titleBrush, titleColor, ms);
            }

            if (_contentBrush != null)
            {
                // базовый цвет контента (Beige) чуть «остужаем» в idle, чтобы не резал глаз
                var contentColor = active
                    ? (Color)ColorConverter.ConvertFromString("#FFF5F5DC")!      // Beige
                    : Color.FromRgb(0xD0, 0xCC, 0xBA);                            // чуть приглушённый Beige
                AnimateColor(_contentBrush, contentColor, ms);
            }

            // на случай возврата из PIN — вернём "остальное" к 100%
            if (IsPinned)
            {
                HeaderTitleArea.Opacity = 1.0;
                CloseArea.Opacity = 1.0;
                ContentArea.Opacity = 1.0;
            }
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
            IsPinned = !IsPinned;
        }


    /// <summary>
    /// Делает видимой только PNG PIN и сужает ЗОНУ контента вдвое при PIN.
    /// Возвращает всё обратно при UNPIN.
    /// </summary>
    private void ApplyPinVisualAndLayout()
    {
        // иконка (если нужно менять на прозрачную/цветную)
        var uri = IsPinned
            ? new Uri("pack://application:,,,/Resources/redPin3.png")       // закреплён
            : new Uri("pack://application:,,,/Resources/redPin3.png"); // обычный
        PinIcon.Source = new BitmapImage(uri);

        // кликабельность: при PIN только сам PIN доступен
        HeaderTitleArea.IsHitTestVisible = !IsPinned;
        CloseArea.IsHitTestVisible = !IsPinned;
        CopyArea.IsHitTestVisible = !IsPinned;
        BubbleBackground.IsHitTestVisible = !IsPinned;
        ResizeArea.IsHitTestVisible = !IsPinned;
        TopPanelBackground.Opacity = 1.0;

        if (IsPinned && _isTitleEditing) CancelTitleEdit();

        if (IsPinned)
        {
            // затемняем «остальное»; PinArea визуально управляется стилем в XAML
            HeaderTitleArea.Opacity = OPACITY_PIN_OTHERS;
            CloseArea.Opacity = OPACITY_PIN_OTHERS;
            ContentArea.Opacity = OPACITY_PIN_OTHERS;
            BubbleBackground.Opacity = OPACITY_PIN_OTHERS;
            CopyArea.Opacity = OPACITY_PIN_OTHERS;
            TopPanelBackground.Opacity = OPACITY_PIN_OTHERS;
            ResizeArea.Opacity = OPACITY_PIN_OTHERS;

            HeaderTitleArea.IsHitTestVisible = false;
            CloseArea.IsHitTestVisible = false;
            CopyArea.IsHitTestVisible = false;
            ResizeArea.IsHitTestVisible = false;
            BubbleBackground.IsHitTestVisible = false;
            ContentArea.IsHitTestVisible = false;
            TopPanelBackground.IsHitTestVisible = false;

            // КЛЮЧЕВОЕ: Transparent ловит клики, null — пропускает
            Root.Background = null;

            // сузить ЗОНУ контента вдвое
            Dispatcher.BeginInvoke(() =>
            {
                var headerH = HeaderRow.ActualHeight > 0 ? HeaderRow.ActualHeight : 25;
                var contentH = ContentRow.ActualHeight > 0 ? ContentRow.ActualHeight : Math.Max(60, ActualHeight - headerH);
                var pinnedH = Math.Max(40, contentH / 2.0);
                ContentRow.Height = new GridLength(pinnedH, GridUnitType.Pixel);
                ContentScroll.MaxHeight = pinnedH - ContentArea.Padding.Top - ContentArea.Padding.Bottom;
            }, DispatcherPriority.Loaded);
        }
        else
        {
            // вернуть видимость
            HeaderTitleArea.Opacity = 1.0;
            CloseArea.Opacity = 1.0;
            ContentArea.Opacity = 1.0;
            CopyArea.Opacity = 1.0;
            BubbleBackground.Opacity = 1.0;
            TopPanelBackground.Opacity = 1.0;
            ResizeArea.Opacity = 1.0;
            PinArea.Opacity = 1.0;

            // Возвращаем как было
            HeaderTitleArea.IsHitTestVisible = true;
            CloseArea.IsHitTestVisible = true;
            CopyArea.IsHitTestVisible = true;
            ResizeArea.IsHitTestVisible = true;
            BubbleBackground.IsHitTestVisible = true;
            ContentArea.IsHitTestVisible = true;
            TopPanelBackground.IsHitTestVisible = true;

            Root.Background = Brushes.Transparent;

            // вернуть «звёздочку» строке контента и ограничения скролла
            ContentRow.Height = _contentStar;
            ContentScroll.ClearValue(ScrollViewer.MaxHeightProperty);
        }
    }

    // ======== Drag lifecycle ========

    private void Root_MouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (IsPinned) return;

        var src = e.OriginalSource as DependencyObject;

        // исключаем клик по пину/кресту/редактору
        if (IsWithin(src, PinBtn) ||
        IsWithin(src, CloseBtn) ||
        IsWithin(src, CopyBtn) ||
        IsWithin(src, ResizeBtn) ||
        IsWithin(src, ResizeArea) ||
        IsWithin(src, TitleEdit))
        {
            // даём событию пройти вглубь до Button, не мешаем Click
            return;
        }

        // даблклик по заголовку — редактирование
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
        UpdateOpacityState();
    }

        private void Root_MouseMove(object? sender, MouseEventArgs e)
        {
            if (IsPinned || _isTitleEditing) return;
            if (!_drag.IsDown) return;

            var local = e.GetPosition(this);
            var screen = PointToScreen(local);

            var wasDragging = _drag.IsDragging;
            _drag.MaybeBeginDrag(screen);
            _drag.OnMove(screen);
            if (_drag.IsDragging && !wasDragging) UpdateOpacityState();

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
                    var hostLocalAtCursor = _host.ScreenToLocal(screen);
                    var newTopLeft = new Point(hostLocalAtCursor.X - _localGrab.X,
                                                hostLocalAtCursor.Y - _localGrab.Y);
                    Canvas.SetLeft(this, newTopLeft.X);
                    Canvas.SetTop(this, newTopLeft.Y);

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


                        // ВАЖНО: включить click-through, если pinned
                        _floatWindow.ClickThroughWhenPinned = IsPinned;

                    if (!IsMouseCaptured) CaptureMouse();
                        UpdateOpacityState();
                    }
                }
            }
        }

        private void Root_MouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            var wasTap = _drag.IsTapNow();
            _hoverWatch.Stop();

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

                    // Вернулись на холст — тут уже контрол сам хранит размер
                    Width = VM.Width;
                    Height = VM.Height;
                    HorizontalAlignment = HorizontalAlignment.Left;
                    VerticalAlignment = VerticalAlignment.Top;

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

            if (_hoverArmed)
            {
                bool ok = TryPasteAtCursorThroughBubble(VM.ContentText ?? string.Empty);
                if (ok) RequestClose();
            }

            ArmReset();
            _drag.Reset();
            UpdateOpacityState();

            if (wasTap) e.Handled = true;
        }

        private async void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            var text = VM?.ContentText ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                Logger.Info("Copy: ContentText is empty — nothing to copy.");
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            if (TrySetClipboardText(text, out var err))
            {
                Logger.Info($"Copy: copied to clipboard, len={text.Length}.");

                // Небольшой визуальный отклик на кнопке Copy
                //var oldBg = CopyArea.Background;
                //try
                //{
                //    CopyArea.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x2E, 0x7D, 0x32)); // зелёный полупрозрачный
                //    await Task.Delay(150);
                //}
                //finally
                //{
                //    CopyArea.Background = oldBg;
                //}
            }
            else
            {
                Logger.Error("Copy: clipboard set failed: " + err);
                System.Media.SystemSounds.Beep.Play(); 
            }
        }

        /// <summary>
        /// Безопасно положить текст в буфер обмена (повторяем попытки, если занят).
        /// </summary>
        private static bool TrySetClipboardText(string text, out string? error)
        {
            error = null;

            // 6 попыток с нарастающей задержкой
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    // Для надёжности можно так:
                    // Clipboard.SetDataObject(text, true);
                    Clipboard.SetText(text);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Thread.Sleep(50 + i * 50);
                }
            }
            return false;
        }

        // ======== Редактирование заголовка ========

        private void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPinned) return; // было: _isLocked
            if (e.ClickCount == 2) { BeginTitleEdit(); e.Handled = true; }
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
            if (e.Key == Key.Enter) { CommitTitleEdit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelTitleEdit(); e.Handled = true; }
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

        // ======== Прозрачность ========

        private void UpdateOpacityState()
        {
            if (IsPinned)
            {
                // В PIN Root делаем 1.0 (иначе потушим саму PNG), «дымка» уже расставлена выше
                Root.Opacity = 1.0;
                return;
            }

            double target;
            if (_drag.IsDragging) target = OPACITY_DRAG;
            else if (_isPointerOver) target = OPACITY_HOVER;
            else target = OPACITY_IDLE;

            Root.Opacity = target;

            // на случай возврата из PIN — вернуть нормальную видимость
            HeaderTitleArea.Opacity = 1.0;
            CloseArea.Opacity = 1.0;
            ContentArea.Opacity = 1.0;
        }

    // ======== Вставка «сквозь» пузырь ========

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
            for (var d = src; d != null; d = ParentOf(d))
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

    }
}
