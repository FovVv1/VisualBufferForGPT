using System;
using System.Windows;

namespace VisualBuffer.Services
{
    /// <summary>
    /// Помощник для различения «тапа» и «драга» + хранение состояния нажатия.
    /// Порог: расстояние ~6px или время >170мс -> считаем, что начался drag.
    /// </summary>
    public sealed class DragHelper
    {
        public Point DownCanvas { get; private set; }
        public Point DownScreen { get; private set; }
        public Point LastScreen { get; private set; }
        public DateTime DownTime { get; private set; }

        public bool IsDown { get; private set; }
        public bool IsDragging { get; private set; }

        /// <summary>Порог расстояния (px, экранные координаты), после которого стартует drag.</summary>
        public double DistanceThreshold { get; set; } = 6;

        /// <summary>Порог времени «короткого тапа».</summary>
        public TimeSpan TimeThreshold { get; set; } = TimeSpan.FromMilliseconds(170);

        public void Reset()
        {
            IsDown = false;
            IsDragging = false;
            DownCanvas = default;
            DownScreen = default;
            LastScreen = default;
            DownTime = default;
        }

        /// <summary>Фиксируем нажатие.</summary>
        public void OnDown(Point canvasPos, Point screenPos, DateTime? now = null)
        {
            IsDown = true;
            IsDragging = false;
            DownCanvas = canvasPos;
            DownScreen = screenPos;
            LastScreen = screenPos;
            DownTime = now ?? DateTime.UtcNow;
        }

        /// <summary>Обновляем текущую позицию (обычно в MouseMove).</summary>
        public void OnMove(Point screenPos)
        {
            LastScreen = screenPos;
        }

        /// <summary>Проверяем, не пора ли перейти в drag по расстоянию/времени.</summary>
        public bool MaybeBeginDrag(Point screenPos, DateTime? now = null)
        {
            if (!IsDown || IsDragging)
            {
                LastScreen = screenPos;
                return IsDragging;
            }

            var dt = (now ?? DateTime.UtcNow) - DownTime;
            var dx = screenPos.X - DownScreen.X;
            var dy = screenPos.Y - DownScreen.Y;
            var dist2 = dx * dx + dy * dy;

            if (dist2 >= DistanceThreshold * DistanceThreshold || dt > TimeThreshold)
                IsDragging = true;

            LastScreen = screenPos;
            return IsDragging;
        }

        /// <summary>Это короткий тап (не drag) в текущий момент?</summary>
        public bool IsTapNow(DateTime? now = null, Point? screenPos = null)
        {
            if (!IsDown) return false;

            var dt = (now ?? DateTime.UtcNow) - DownTime;
            var p = screenPos ?? LastScreen;
            var dx = p.X - DownScreen.X;
            var dy = p.Y - DownScreen.Y;
            var dist2 = dx * dx + dy * dy;

            return !IsDragging &&
                   dt <= TimeThreshold &&
                   dist2 < DistanceThreshold * DistanceThreshold;
        }

        /// <summary>Поднимаем кнопку мыши: завершаем жест.</summary>
        public void OnUp()
        {
            IsDown = false;
            IsDragging = false;
        }
    }
}
