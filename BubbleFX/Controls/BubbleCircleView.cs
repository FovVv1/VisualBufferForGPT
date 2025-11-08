using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VisualBuffer.BubbleFX.UI.Canvas;      // BubbleHostCanvas
using VisualBuffer.BubbleFX.Utils;          // DpiUtil
using Services = VisualBuffer.Services;     // DragHelper

namespace VisualBuffer.BubbleFX.Controls
{
    /// <summary>
    /// Круглый пузырь: DP Diameter, DP Text, DP Title.
    /// Шаблон — Themes/Generic.xaml. Перетаскивается ТОЛЬКО внутри круга.
    /// Поддерживает авто-подбор диаметра по контенту с анимацией.
    /// </summary>
    public class BubbleCircleView : Control
    {
        private readonly Services.DragHelper _drag = new();
        private Window? _floatWindow;              // во флоат-окне
        private Canvas? _plainCanvas;              // на обычном Canvas
        private BubbleHostCanvas? _host;           // твой BubbleHostCanvas
        private Point _localGrab;

        private Button? _btnPin;
        private Button? _btnClose;

        static BubbleCircleView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(typeof(BubbleCircleView)));
        }

        public BubbleCircleView()
        {
            Focusable = true;

            AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), true);
            AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), true);
            AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp), true);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        #region DP: Title / IsPinned

        /// <summary>Заголовок для вывода по дуге.</summary>
        public string? Title
        {
            get => (string?)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata("Bubble", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

        /// <summary>Состояние пина (для внешней логики и визуала).</summary>
        public bool IsPinned
        {
            get => (bool)GetValue(IsPinnedProperty);
            set => SetValue(IsPinnedProperty, value);
        }
        public static readonly DependencyProperty IsPinnedProperty =
            DependencyProperty.Register(
                nameof(IsPinned),
                typeof(bool),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(false));

        // события
        public static readonly RoutedEvent CloseRequestedEvent =
            EventManager.RegisterRoutedEvent(nameof(CloseRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BubbleCircleView));
        public event RoutedEventHandler CloseRequested { add => AddHandler(CloseRequestedEvent, value); remove => RemoveHandler(CloseRequestedEvent, value); }

        public static readonly RoutedEvent PinnedChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(PinnedChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(BubbleCircleView));
        public event RoutedEventHandler PinnedChanged { add => AddHandler(PinnedChangedEvent, value); remove => RemoveHandler(PinnedChangedEvent, value); }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // отписаться от старых, если шаблон переустановили
            if (_btnPin != null) _btnPin.Click -= OnPinClick;
            if (_btnClose != null) _btnClose.Click -= OnCloseClick;

            _btnPin = GetTemplateChild("PART_PinButton") as Button;
            _btnClose = GetTemplateChild("PART_CloseButton") as Button;

            if (_btnPin != null) _btnPin.Click += OnPinClick;
            if (_btnClose != null) _btnClose.Click += OnCloseClick;
        }

        private void OnPinClick(object? sender, RoutedEventArgs e)
        {
            IsPinned = !IsPinned;
            RaiseEvent(new RoutedEventArgs(PinnedChangedEvent, this));
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
        }

        #endregion

        #region DP: Diameter (+ Min/Max + AutoSizeToContent)

        public double Diameter
        {
            get => (double)GetValue(DiameterProperty);
            set => SetValue(DiameterProperty, value);
        }

        public static readonly DependencyProperty DiameterProperty =
            DependencyProperty.Register(
                nameof(Diameter),
                typeof(double),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(
                    240.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public double MinDiameter
        {
            get => (double)GetValue(MinDiameterProperty);
            set => SetValue(MinDiameterProperty, value);
        }
        public static readonly DependencyProperty MinDiameterProperty =
            DependencyProperty.Register(
                nameof(MinDiameter),
                typeof(double),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(160.0));

        public double MaxDiameter
        {
            get => (double)GetValue(MaxDiameterProperty);
            set => SetValue(MaxDiameterProperty, value);
        }
        public static readonly DependencyProperty MaxDiameterProperty =
            DependencyProperty.Register(
                nameof(MaxDiameter),
                typeof(double),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(560.0));

        public bool AutoSizeToContent
        {
            get => (bool)GetValue(AutoSizeToContentProperty);
            set => SetValue(AutoSizeToContentProperty, value);
        }
        public static readonly DependencyProperty AutoSizeToContentProperty =
            DependencyProperty.Register(
                nameof(AutoSizeToContent),
                typeof(bool),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(true, OnAutoSizeFlagChanged));

        private static void OnAutoSizeFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (BubbleCircleView)d;
            if (v.IsLoaded && v.AutoSizeToContent)
                v.AutoFitDiameterToContent(animated: true);
        }

        #endregion

        #region DP: Text

        public string? Text
        {
            get => (string?)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                    OnTextChanged));

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var v = (BubbleCircleView)d;
            if (!v.AutoSizeToContent) return;
            if (v.IsLoaded) v.AutoFitDiameterToContent(animated: true);
        }

        #endregion

        #region визуал и загрузка

        private void OnLoaded(object? s, RoutedEventArgs e)
        {
            _floatWindow = Window.GetWindow(this);
            _host = FindAncestor<BubbleHostCanvas>(this);
            _plainCanvas = FindAncestor<Canvas>(this);

            if (AutoSizeToContent)
                AutoFitDiameterToContent(animated: false);
        }

        private void OnUnloaded(object? s, RoutedEventArgs e)
        {
            _drag.Reset();
            _floatWindow = null;
            _host = null;
            _plainCanvas = null;

            if (_btnPin != null) _btnPin.Click -= OnPinClick;
            if (_btnClose != null) _btnClose.Click -= OnCloseClick;
        }

        private static T? FindAncestor<T>(DependencyObject start) where T : class
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T hit) return hit;
            return null;
        }

        private bool IsPointInsideCircle(Point p)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return false;

            double r = Math.Min(w, h) * 0.5;
            Point c = new(w * 0.5, h * 0.5);
            double dx = p.X - c.X;
            double dy = p.Y - c.Y;
            return (dx * dx + dy * dy) <= (r * r);
        }

        #endregion

        #region авто-подбор Diameter

        private void AutoFitDiameterToContent(bool animated)
        {
            var text = Text ?? string.Empty;
            text = text.Replace("\r\n", "\n");

            if (string.IsNullOrEmpty(text))
            {
                AnimateOrSetDiameter(MinDiameter, animated);
                return;
            }

            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            double maxLineWidth = 0;
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
#pragma warning disable CS0618
                var ft = new FormattedText(
                    line,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    Brushes.Transparent,
                    pixelsPerDip);
#pragma warning restore CS0618
                maxLineWidth = Math.Max(maxLineWidth, ft.WidthIncludingTrailingWhitespace);
            }

            Thickness pad = Padding;
            const double extra = 14.0;
            double target = maxLineWidth + pad.Left + pad.Right + extra;

            target = Math.Max(MinDiameter, Math.Min(MaxDiameter, target));
            AnimateOrSetDiameter(target, animated);
        }

        private void AnimateOrSetDiameter(double target, bool animated)
        {
            if (!animated)
            {
                BeginAnimation(DiameterProperty, null);
                Diameter = target;
                return;
            }

            var da = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(DiameterProperty, da);
        }

        #endregion

        #region drag only inside circle

        private void OnPreviewMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            var local = e.GetPosition(this);
            if (!IsPointInsideCircle(local))
                return;

            Focus();
            CaptureMouse();

            var screen = PointToScreen(local);
            _localGrab = local;
            _drag.OnDown(local, screen, DateTime.UtcNow);
            e.Handled = true;
        }

        private void OnPreviewMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_drag.IsDown) return;

            var local = e.GetPosition(this);
            var screen = PointToScreen(local);

            _drag.MaybeBeginDrag(screen);
            _drag.OnMove(screen);
            if (!_drag.IsDragging) return;

            if (_host != null)
            {
                var hostLocal = _host.ScreenToLocal(screen);
                var newTopLeft = new Point(hostLocal.X - _localGrab.X, hostLocal.Y - _localGrab.Y);
                Canvas.SetLeft(this, newTopLeft.X);
                Canvas.SetTop(this, newTopLeft.Y);
            }
            else if (_plainCanvas != null)
            {
                var canvasPt = Mouse.GetPosition(_plainCanvas);
                var newTopLeft = new Point(canvasPt.X - _localGrab.X, canvasPt.Y - _localGrab.Y);
                Canvas.SetLeft(this, newTopLeft.X);
                Canvas.SetTop(this, newTopLeft.Y);
            }
            else if (_floatWindow != null)
            {
                var (sx, sy) = DpiUtil.GetDpiScale(_floatWindow);
                _floatWindow.Left = screen.X / sx - _localGrab.X;
                _floatWindow.Top = screen.Y / sy - _localGrab.Y;
            }

            e.Handled = true;
        }

        private void OnPreviewMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            bool wasTap = _drag.IsTapNow();
            _drag.Reset();

            // здесь позже можно включить EDIT-режим (двойной/одинарный клик)
            e.Handled = true;
        }

        #endregion
    }
}
