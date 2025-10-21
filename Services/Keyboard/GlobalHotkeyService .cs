using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.Services.Keyboard
{
    /// <summary>
    /// Минимальный глобальный хук клавиатуры:
    ///  • Детектит DoubleCopy (двойной Ctrl+C) — только событие, НИЧЕГО не блокирует.
    ///  • НЕ трогает Ctrl+V, не реинжектит клавиши.
    ///  • Игнорирует инъецированные события (SendInput/keybd_event).
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        public static GlobalHotkeyService Instance { get; } = new();

        public event EventHandler? DoubleCopy;

        private LowLevelKeyboardProc? _proc;
        private nint _hookId;

        // Настройки/состояние
        private const int DoubleCopyWindowMs = 450; // окно между двумя Ctrl+C
        private DateTime _lastCtrlCUtc = DateTime.MinValue;

        private GlobalHotkeyService() { }

        public void Install()
        {
            if (_hookId != nint.Zero) return;
            _proc = HookCallback;
            // Для WH_KEYBOARD_LL достаточно хендла текущего модуля
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            Logger.Info("GlobalHotkeyService hook installed.");
        }

        public void Dispose()
        {
            try
            {
                if (_hookId != nint.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = nint.Zero;
                    Logger.Info("GlobalHotkeyService hook uninstalled.");
                }
                _proc = null;
            }
            catch { /* never throw из логгера */ }
        }

        // ===== Hook =====

        private nint HookCallback(int nCode, nint wParam, nint lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // Игнорируем инъецированные
                    const int LLKHF_INJECTED = 0x10;
                    const int LLKHF_LOWER_IL_INJECTED = 0x02;
                    bool injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                    if (!injected)
                    {
                        bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                        // читаем текущее состояние Ctrl глобально
                        bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                        if (isKeyDown && ctrlDown && data.vkCode == VK_C)
                        {
                            var now = DateTime.UtcNow;
                            if ((now - _lastCtrlCUtc).TotalMilliseconds <= DoubleCopyWindowMs)
                            {
                                _lastCtrlCUtc = DateTime.MinValue;
                                try { DoubleCopy?.Invoke(this, EventArgs.Empty); } catch { }
                                Logger.Info("DoubleCopy detected.");
                            }
                            else
                            {
                                _lastCtrlCUtc = now;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in GlobalHotkeyService.HookCallback.", ex);
            }

            // НИКОГДА не блокируем — пропускаем дальше
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ===== Win32 =====

        private const int WH_KEYBOARD_LL = 13;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public nint dwExtraInfo;
        }

        private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
        [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern nint GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    }
}
