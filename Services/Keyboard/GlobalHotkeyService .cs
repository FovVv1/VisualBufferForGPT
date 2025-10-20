// Services/GlobalHotkeyService.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.Services.Keyboard
{
    public sealed class GlobalHotkeyService : IDisposable
    {
        public event EventHandler? DoubleCopy;
        public event EventHandler<PasteHoldContext>? PasteHoldStart;
        public event EventHandler? PasteHoldEnd;

        private LowLevelKeyboardProc? _proc;
        private IntPtr _hookId = IntPtr.Zero;

        // Config
        private const int DoubleCopyWindowMs = 450;

        // State
        private DateTime _lastCtrlC = DateTime.MinValue;
        private bool _pasteHoldArmed;
        private IntPtr _pasteHoldFg;

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            _proc = HookCallback;
            using var cur = Process.GetCurrentProcess();
            using var mod = cur.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
            Logger.Info("GlobalHotkeyService hook installed.");
        }

        public void Dispose()
        {
            try
            {
                if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                _proc = null;
            }
            catch { /* never throw */ }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var msg = (int)wParam;
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // ВАЖНО: пропускаем И НЕ УЧИТЫВАЕМ инжектированные события
                bool injected =
                    (data.flags & LLKHF_INJECTED) != 0 ||
                    (data.flags & LLKHF_LOWER_IL_INJECTED) != 0;

                // мы ничего не блокируем вообще — всегда CallNextHookEx
                // но для своей логики учитываем только НЕинжектированные события
                if (!injected)
                {
                    bool isCtrlDown = IsCtrlPressed();
                    bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    // Double Copy: дважды Ctrl+C подряд в узком окне времени
                    if (isKeyDown && isCtrlDown && data.vkCode == VK_C)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastCtrlC).TotalMilliseconds <= DoubleCopyWindowMs)
                        {
                            _lastCtrlC = DateTime.MinValue;
                            try { DoubleCopy?.Invoke(this, EventArgs.Empty); } catch { }
                        }
                        else
                        {
                            _lastCtrlC = now;
                        }
                    }

                    // PasteHold — по вашему желанию: пример — зажатый Ctrl+V
                    if (isKeyDown && isCtrlDown && data.vkCode == VK_V && !_pasteHoldArmed)
                    {
                        _pasteHoldArmed = true;
                        _pasteHoldFg = GetForegroundWindow();
                        try { PasteHoldStart?.Invoke(this, new PasteHoldContext(_pasteHoldFg)); } catch { }
                    }
                    if (isKeyUp && data.vkCode == VK_V && _pasteHoldArmed)
                    {
                        _pasteHoldArmed = false;
                        try { PasteHoldEnd?.Invoke(this, EventArgs.Empty); } catch { }
                    }
                }
            }

            // НИЧЕГО НЕ БЛОКИРУЕМ
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        public sealed record PasteHoldContext(IntPtr ForegroundHwnd);

        // Helpers
        private static bool IsCtrlPressed()
            => (GetKeyState(VK_CONTROL) & 0x8000) != 0;

        // Win32

        private const int WH_KEYBOARD_LL = 13;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;

        private const int LLKHF_INJECTED = 0x10;
        private const int LLKHF_LOWER_IL_INJECTED = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    }
}
