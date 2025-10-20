using System.Windows;
using VisualBuffer.BubbleFX.Controls;
using VisualBuffer.BubbleFX.Models;

namespace VisualBuffer.BubbleFX.UI.Bubble
{
    public partial class BubbleWindow : Window
    {
        public BubbleWindow()
        {
            InitializeComponent();
        }

        // НОВАЯ перегрузка: позволяет передать текст
        public BubbleWindow(string text) : this()
        {
            var vm = new BubbleViewModel
            {
                Title = "Bubble",
                Width = 300,
                Height = 180,
                IsFloating = true,
                X = 0,
                Y = 0,
                ContentText = text,   // <- см. п.3
            };

            var view = new BubbleView { DataContext = vm };
            Content = view;
        }
    }
}
