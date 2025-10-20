using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VisualBuffer.Services.Clipboard
{
    public sealed class ClipboardListener : IDisposable
    {
        private HwndSource? _src;
        public event EventHandler<string>? ClipboardTextCopied;

        public ClipboardListener()
        {
            var wnd = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            wnd.SourceInitialized += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(wnd).Handle;
                _src = HwndSource.FromHwnd(hwnd);
                _src.AddHook(WndProc);
                AddClipboardFormatListener(hwnd);
            };
            wnd.Show();
            wnd.Hide();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_CLIPBOARDUPDATE = 0x031D;
            if (msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        var text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                        ClipboardCache.Instance.SetLast(text);
                        ClipboardTextCopied?.Invoke(this, text);
                    }
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public void Dispose()
        {
            if (_src is null) return;
            RemoveClipboardFormatListener(_src.Handle);
            _src.RemoveHook(WndProc);
            _src.Dispose();
        }
    }
}
