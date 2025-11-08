using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisualBuffer.BubbleFX.Utils;

namespace VisualBuffer.BubbleFX.Controls
{
    /// <summary>
    /// Минимальный «круглый пузырь»: имеет DP Diameter и DP Text.
    /// Шаблон задаётся в Themes/Generic.xaml (или BubbleCircleTemplate.xaml).
    /// </summary>
    public class BubbleCircleView : Control
    {
        private Services.DragHelper _drag = new();
        private Window? _floatWindow;              // если во флоат-режиме
        private Canvas? _plainCanvas;              // если висим на обычном Canvas
        private BubbleHostCanvas? _host;           // если используешь ваш BubbleHostCanvas
        private Point _localGrab;
        private const double MoveSlopPx = 3.0;
        static BubbleCircleView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(typeof(BubbleCircleView)));
        }

        public double Diameter
        {
            get => (double)GetValue(DiameterProperty);
            set => SetValue(DiameterProperty, value);
        }
        public static readonly DependencyProperty DiameterProperty =
            DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        

        public BubbleCircleView()
        {
            // превью-события — как в старом BubbleView
            AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), true);
            AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), true);
            AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp), true);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static bool ValidateDiameter(object value) => (double)value >= 24.0;
        private static void OnDiameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // здесь ничего не нужно — TemplateBinding сам обновит размеры корня
        }

        // ---- Text (контент для отображения) ----
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
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        // Рекомендуемые визуальные параметры
        public Brush BubbleBackground
        {
            get => (Brush)GetValue(BubbleBackgroundProperty);
            set => SetValue(BubbleBackgroundProperty, value);
        }

        public static readonly DependencyProperty BubbleBackgroundProperty =
            DependencyProperty.Register(
                nameof(BubbleBackground),
                typeof(Brush),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))));

        public Brush BubbleOutline
        {
            get => (Brush)GetValue(BubbleOutlineProperty);
            set => SetValue(BubbleOutlineProperty, value);
        }

        public static readonly DependencyProperty BubbleOutlineProperty =
            DependencyProperty.Register(
                nameof(BubbleOutline),
                typeof(Brush),
                typeof(BubbleCircleView),
                new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D))));

        private bool IsPointInsideCircle(Point p)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return false;

            // круг вписан в доступную область
            double r = Math.Min(w, h) * 0.5;
            Point c = new(w * 0.5, h * 0.5);

            double dx = p.X - c.X;
            double dy = p.Y - c.Y;
            return (dx * dx + dy * dy) <= (r * r);
        }

        private void OnLoaded(object? s, RoutedEventArgs e)
        {
            _floatWindow = Window.GetWindow(this);

            // если внутри кастомного холста — запомним его
            _host = FindAncestor<BubbleHostCanvas>(this);

            // если просто внутри обычного Canvas — тоже ок
            _plainCanvas = FindAncestor<Canvas>(this);
        }

        private void OnUnloaded(object? s, RoutedEventArgs e)
        {
            _drag.Reset();
            _floatWindow = null;
            _host = null;
            _plainCanvas = null;
        }

        private static T? FindAncestor<T>(DependencyObject start) where T : class
        {
            for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is T hit) return hit;
            return null;
        }

        private void OnPreviewMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            var local = e.GetPosition(this);
            if (!IsPointInsideCircle(local))
                return; // клики мимо круга — игнор (даже если хост что-то отдаст)

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

            // Перемещение
            if (_host != null)
            {
                // Докнут на ваш BubbleHostCanvas
                var hostLocal = _host.ScreenToLocal(screen);
                var newTopLeft = new Point(hostLocal.X - _localGrab.X, hostLocal.Y - _localGrab.Y);

                Canvas.SetLeft(this, newTopLeft.X);
                Canvas.SetTop(this, newTopLeft.Y);
            }
            else if (_plainCanvas != null)
            {
                // На обычном Canvas
                var canvasPt = Mouse.GetPosition(_plainCanvas);
                var newTopLeft = new Point(canvasPt.X - _localGrab.X, canvasPt.Y - _localGrab.Y);
                Canvas.SetLeft(this, newTopLeft.X);
                Canvas.SetTop(this, newTopLeft.Y);
            }
            else if (_floatWindow != null)
            {
                // Плавающее окно
                var (sx, sy) = DpiUtil.GetDpiScale(_floatWindow); // твой утилитарный метод
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

            if (wasTap)
            {
                // здесь позже можно включить режим редактирования контента (двойное нажатие и т.п.)
                // пока оставим пустым
            }

            e.Handled = true;
        }
    }
}
