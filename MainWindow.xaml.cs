using System.Windows;
using System.Windows.Controls;
using VisualBuffer.BubbleFX.Controls;
using VisualBuffer.BubbleFX.Models;

namespace VisualBuffer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Add a couple of bubbles
            AddBubble(60, 60, "Paste Helper");
            AddBubble(320, 140, "Translator");
        }

        void AddBubble(double x, double y, string title)
        {
            var vm = new BubbleViewModel { X = x, Y = y, Title = title };
            var view = new BubbleView { DataContext = vm, Width = vm.Width, Height = vm.Height };
            Host.AddBubble(view, new Point(x, y));
        }
    }
}
