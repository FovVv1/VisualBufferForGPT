using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;


namespace VisualBuffer.BubbleFX.Models
{
    public class BubbleViewModel : INotifyPropertyChanged
    {
        private bool _isFloating;
        private double _x;
        private double _y;
        private double _width = 320;
        private double _height = 200;
        private string _title = "Bubble";


        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // внутри класса BubbleViewModel
        private string _contentText = string.Empty;
        public string ContentText
        {
            get => _contentText;
            set { if (_contentText != value) { _contentText = value; OnPropertyChanged(nameof(ContentText)); } }
        }

        public bool IsFloating
        {
            get => _isFloating;
            set { if (_isFloating != value) { _isFloating = value; OnPropertyChanged(); } }
        }


        public double X
        {
            get => _x; set { if (_x != value) { _x = value; OnPropertyChanged(); } }
        }
        public double Y
        {
            get => _y; set { if (_y != value) { _y = value; OnPropertyChanged(); } }
        }


        public double Width
        {
            get => _width; set { if (_width != value) { _width = value; OnPropertyChanged(); } }
        }
        public double Height
        {
            get => _height; set { if (_height != value) { _height = value; OnPropertyChanged(); } }
        }


        public string Title
        {
            get => _title; set { if (_title != value) { _title = value; OnPropertyChanged(); } }
        }


        // Place for your payload (text, UI state, etc.)
    }
}