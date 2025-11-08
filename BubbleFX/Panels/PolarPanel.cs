using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VisualBuffer.BubbleFX.Panels
{
    /// <summary>
    /// Полярная панель: раскладывает дочерние элементы по дуге окружности.
    /// - Равномерно распределяет по дуге [StartAngleDeg..EndAngleDeg], если явные углы не заданы.
    /// - Центр можно задать явно (CenterX/CenterY) или оставить автоположение — центр панели.
    /// - Радиус задаёт расстояние от центра до ЦЕНТРА ребёнка.
    /// - Если Radius = double.NaN => используется авто-радиус: min(Width, Height)/2 + ItemRadiusOffset.
    /// - Индивидуальный угол для ребёнка можно задать через attached PolarPanel.Angle (в градусах).
    ///   0° — вправо, 90° — вверх, 180° — влево, 270° — вниз.
    /// </summary>
    public sealed class PolarPanel : Panel
    {
        #region DP: CenterX / CenterY (NaN => центр панели)
        public double CenterX
        {
            get => (double)GetValue(CenterXProperty);
            set => SetValue(CenterXProperty, value);
        }
        public static readonly DependencyProperty CenterXProperty =
            DependencyProperty.Register(
                nameof(CenterX),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsArrange));

        public double CenterY
        {
            get => (double)GetValue(CenterYProperty);
            set => SetValue(CenterYProperty, value);
        }
        public static readonly DependencyProperty CenterYProperty =
            DependencyProperty.Register(
                nameof(CenterY),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsArrange));
        #endregion

        #region DP: Radius (NaN => авто-радиус)
        /// <summary>
        /// Радиус дуги в DIP (до центра ребёнка).
        /// Установи в double.NaN, чтобы панель сама вычислила радиус = min(size)/2 + ItemRadiusOffset.
        /// </summary>
        public double Radius
        {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }
        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.Register(
                nameof(Radius),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsArrange, null, CoerceNonNegative));

        private static object CoerceNonNegative(DependencyObject d, object baseValue)
        {
            var v = (double)baseValue;
            // Разрешаем NaN (для авто-режима); отрицательные — в 0.
            if (double.IsNaN(v)) return v;
            return v < 0 ? 0.0 : v;
        }
        #endregion

        #region DP: StartAngleDeg / EndAngleDeg (градусы)
        /// <summary>Начальный угол дуги (в градусах). По умолчанию 200°.</summary>
        public double StartAngleDeg
        {
            get => (double)GetValue(StartAngleDegProperty);
            set => SetValue(StartAngleDegProperty, value);
        }
        public static readonly DependencyProperty StartAngleDegProperty =
            DependencyProperty.Register(
                nameof(StartAngleDeg),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>Конечный угол дуги (в градусах). По умолчанию -20°.</summary>
        public double EndAngleDeg
        {
            get => (double)GetValue(EndAngleDegProperty);
            set => SetValue(EndAngleDegProperty, value);
        }
        public static readonly DependencyProperty EndAngleDegProperty =
            DependencyProperty.Register(
                nameof(EndAngleDeg),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(-20.0, FrameworkPropertyMetadataOptions.AffectsArrange));
        #endregion

        #region DP: ItemRadiusOffset
        /// <summary>Добавочный сдвиг по радиусу (положительный — дальше от круга).</summary>
        public double ItemRadiusOffset
        {
            get => (double)GetValue(ItemRadiusOffsetProperty);
            set => SetValue(ItemRadiusOffsetProperty, value);
        }
        public static readonly DependencyProperty ItemRadiusOffsetProperty =
            DependencyProperty.Register(
                nameof(ItemRadiusOffset),
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));
        #endregion

        #region Attached: Angle (персональный угол для ребёнка)
        public static double GetAngle(UIElement element) => (double)element.GetValue(AngleProperty);
        public static void SetAngle(UIElement element, double value) => element.SetValue(AngleProperty, value);

        /// <summary>
        /// Индивидуальный угол (в градусах) для конкретного ребёнка.
        /// Значение double.NaN => использовать авто-распределение.
        /// </summary>
        public static readonly DependencyProperty AngleProperty =
            DependencyProperty.RegisterAttached(
                "Angle",
                typeof(double),
                typeof(PolarPanel),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentArrange));
        #endregion

        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            return new Size(
                double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (cx, cy) = ResolveCenter(finalSize);

            double baseR;
            if (double.IsNaN(Radius))
            {
                // авто-радиус = половина меньшей стороны панели + смещение
                var halfMin = 0.5 * Math.Min(finalSize.Width, finalSize.Height);
                baseR = Math.Max(0, halfMin + ItemRadiusOffset);
            }
            else
            {
                baseR = Math.Max(0, Radius + ItemRadiusOffset);
            }

            // Список видимых детей
            var children = new List<UIElement>(InternalChildren.Count);
            foreach (UIElement child in InternalChildren)
            {
                if (child == null || child.Visibility == Visibility.Collapsed) continue;
                children.Add(child);
            }
            if (children.Count == 0) return finalSize;

            // Углы
            var angles = new double[children.Count];
            bool anyExplicit = false;
            for (int i = 0; i < children.Count; i++)
            {
                double a = GetAngle(children[i]);
                angles[i] = a;
                if (!double.IsNaN(a)) anyExplicit = true;
            }

            if (!anyExplicit)
            {
                if (children.Count == 1)
                {
                    angles[0] = MidAngle(StartAngleDeg, EndAngleDeg);
                }
                else
                {
                    double step = (EndAngleDeg - StartAngleDeg) / (children.Count - 1);
                    for (int i = 0; i < children.Count; i++)
                        angles[i] = StartAngleDeg + step * i;
                }
            }
            else
            {
                // распределяем только те, у кого угол не задан
                var auto = Enumerable.Range(0, children.Count).Where(i => double.IsNaN(angles[i])).ToList();
                if (auto.Count > 0)
                {
                    if (auto.Count == 1)
                    {
                        angles[auto[0]] = MidAngle(StartAngleDeg, EndAngleDeg);
                    }
                    else
                    {
                        double step = (EndAngleDeg - StartAngleDeg) / (auto.Count - 1);
                        for (int j = 0; j < auto.Count; j++)
                            angles[auto[j]] = StartAngleDeg + step * j;
                    }
                }
            }

            // Arrange по углам
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var desired = child.DesiredSize;

                double rad = Deg2Rad(angles[i]);
                double x = cx + baseR * Math.Cos(rad) - desired.Width * 0.5;
                double y = cy - baseR * Math.Sin(rad) - desired.Height * 0.5;

                child.Arrange(new Rect(new Point(x, y), desired));
            }

            return finalSize;
        }

        private (double cx, double cy) ResolveCenter(Size size)
        {
            double cx = double.IsNaN(CenterX) ? size.Width * 0.5 : CenterX;
            double cy = double.IsNaN(CenterY) ? size.Height * 0.5 : CenterY;
            return (cx, cy);
        }

        private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;
        private static double MidAngle(double a, double b) => a + (b - a) * 0.5;
    }
}
