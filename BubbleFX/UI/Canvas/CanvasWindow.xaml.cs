using System.Collections.ObjectModel;
using System.Windows;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Insert;

namespace VisualBuffer.BubbleFX.UI.Canvas
{
    public partial class CanvasWindow : Window
    {
        private readonly ObservableCollection<string> _items = new();

        public CanvasWindow()
        {
            InitializeComponent();
            CardsPanel.ItemsSource = _items;
        }

        // холст показывается без активации (ShowActivated=False), чтобы не ломать drag пузыря
        public void ShowOverlayHold()
        {
            Show();
            // НЕ вызывать Activate()
        }

        public void AddItem(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                _items.Insert(0, text);
        }

        // проверка: попадает ли экранная точка в клиентскую область окна
        public bool IsScreenPointInside(int x, int y)
        {
            var local = PointFromScreen(new Point(x, y));
            return local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

        private void OnCardClick(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is string text)
            {
                if (InsertEngine.Instance.HasDeferredTarget)
                    InsertEngine.Instance.PasteToDeferredTarget(text);
                else
                    BubbleManager.Instance.Show(text);
            }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = (string)e.Data.GetData(DataFormats.UnicodeText);
                AddItem(text);
            }
        }
    }
}
