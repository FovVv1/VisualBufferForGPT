using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VisualBuffer.BubbleFX.Primitives
{
    /// <summary>
    /// Декоратор, который:
    /// 1) Клипует содержимое по окружности (вписанной в RenderSize, с Inset).
    /// 2) Делает круглый hit-test: события мыши проходят только внутри круга.
    /// </summary>
    public class CircleHitTestDecorator : Decorator
    {
        public static readonly DependencyProperty InsetProperty =
            DependencyProperty.Register(
                nameof(Inset),
                typeof(double),
                typeof(CircleHitTestDecorator),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange, OnGeomChanged),
                v => (double)v >= 0);

        /// <summary>Внутренний отступ от края вписанной окружности (в DIP).</summary>
        public double Inset
        {
            get => (double)GetValue(InsetProperty);
            set => SetValue(InsetProperty, value);
        }

        public static readonly DependencyProperty EnableClipProperty =
            DependencyProperty.Register(
                nameof(EnableClip),
                typeof(bool),
                typeof(CircleHitTestDecorator),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnGeomChanged));

        /// <summary>Включить/выключить клип по окружности.</summary>
        public bool EnableClip
        {
            get => (bool)GetValue(EnableClipProperty);
            set => SetValue(EnableClipProperty, value);
        }

        private EllipseGeometry? _clipGeom;   // кэш клип-геометрии

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            // Обычная разметка содержимого
            Child?.Arrange(new Rect(arrangeSize));
            UpdateClip(arrangeSize);
            return arrangeSize;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateClip(sizeInfo.NewSize);
        }

        private static void OnGeomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var dec = (CircleHitTestDecorator)d;
            dec.UpdateClip(dec.RenderSize);
            dec.InvalidateVisual();
        }

        private void UpdateClip(Size size)
        {
            if (!EnableClip || size.Width <= 0 || size.Height <= 0)
            {
                Clip = null;
                _clipGeom = null;
                return;
            }

            // Вписываем окружность по минимальному измерению
            double w = size.Width;
            double h = size.Height;
            double d = Math.Max(0, Math.Min(w, h) - Inset * 2.0);
            double r = d / 2.0;

            var cx = w / 2.0;
            var cy = h / 2.0;

            _clipGeom ??= new EllipseGeometry();
            _clipGeom.Center = new Point(cx, cy);
            _clipGeom.RadiusX = r;
            _clipGeom.RadiusY = r;

            // Клип назначаем всему декоратору — клипнется и Child
            Clip = _clipGeom;
        }

        // Круглый hit-test
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        {
            if (_clipGeom == null)
                return base.HitTestCore(hitTestParameters);

            Point p = hitTestParameters.HitPoint;

            // Проверяем точку на попадание в окружность той же геометрией, что и клип.
            if (_clipGeom.FillContains(p))
                return new PointHitTestResult(this, p);

            // Снаружи круга — считаем миссом: мышь «не попала».
            return null;
        }

        // Для удобства можно подсветить границу при debug (опционально)
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            // uncomment при отладке:
            //if (_clipGeom != null)
            //{
            //    dc.DrawGeometry(null, new Pen(Brushes.Magenta, 1), _clipGeom);
            //}
        }

    }
}
