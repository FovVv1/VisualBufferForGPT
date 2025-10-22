using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.Services.Keyboard
{
    /// <summary>
    /// Глобальный низкоуровневый хук клавиатуры:
    ///  • DoubleCopy (двойной Ctrl+C) — событие (ничего не блокируем).
    ///  • Холст: удержание Ctrl+Alt — пока держим, холст показан; отпустили любую — холст скрывается.
    ///  • НЕ трогаем Ctrl+V и не реинжектим клавиши.
    ///  • Игнорируем инъецированные события (SendInput/keybd_event).
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        public static GlobalHotkeyService Instance { get; } = new();

        public event EventHandler? DoubleCopy;

        // Показ/скрытие холста по Ctrl+Alt
        public sealed record CanvasHotkeyContext(nint ForegroundHwnd);
        public event EventHandler<CanvasHotkeyContext>? CanvasShow;
        public event EventHandler? CanvasHide;

        private LowLevelKeyboardProc? _proc;
        private nint _hookId;

        // --- настройки
        private const int DoubleCopyWindowMs = 450;

        // --- состояние
        private DateTime _lastCtrlCUtc = DateTime.MinValue;
        private bool _ctrlDown;
        private bool _altDown;
        private bool _canvasShown;
        private bool _bothDown;
        private GlobalHotkeyService() { }

        public void Install()
        {
            if (_hookId != nint.Zero) return;
            _proc = HookCallback;
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

        // ================= Hook =================


        private nint HookCallback(int nCode, nint wParam, nint lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                    // игнор инъецированных
                    bool injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
                    if (injected) return CallNextHookEx(_hookId, nCode, wParam, lParam);

                    bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    // локальная фиксация состояний по пришедшему событию
                    if (isDown)
                    {
                        if (data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL) _ctrlDown = true;
                        if (data.vkCode == VK_MENU || data.vkCode == VK_LMENU || data.vkCode == VK_RMENU) _altDown = true;
                    }
                    if (isUp)
                    {
                        if (data.vkCode == VK_LCONTROL || data.vkCode == VK_RCONTROL) _ctrlDown = false;
                        if (data.vkCode == VK_MENU || data.vkCode == VK_LMENU || data.vkCode == VK_RMENU) _altDown = false;
                    }

                    // подстраховка: реальные состояния модификаторов
                    bool ctrlNow = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    // Если хочешь именно ЛЕВЫЙ Alt — раскомментируй следующую строку и закомментируй altNow ниже:
                    // bool altNow  = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
                    bool altNow = ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) || (data.flags & LLKHF_ALTDOWN) != 0;

                    // сводим к единой логике
                    if (ctrlNow != _ctrlDown) _ctrlDown = ctrlNow;
                    if (altNow != _altDown) _altDown = altNow;

                    bool bothNow = _ctrlDown && _altDown;

                    // переходы состояния (однократные события)
                    if (bothNow && !_bothDown)
                    {
                        _bothDown = true;
                        var fg = GetForegroundWindow();
                        Logger.Info($"Hotkey: Ctrl+Alt -> show (vk={data.vkCode})");
                        try { CanvasShow?.Invoke(this, new CanvasHotkeyContext(fg)); } catch { }
                    }
                    else if (!bothNow && _bothDown && isUp) // скрыть, когда отпускается одна из клавиш
                    {
                        _bothDown = false;
                        Logger.Info("Hotkey: Ctrl+Alt released -> hide");
                        try { CanvasHide?.Invoke(this, EventArgs.Empty); } catch { }
                    }

                    // DoubleCopy (Ctrl+C×2) — как было
                    if (isDown && ctrlNow && data.vkCode == VK_C)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - _lastCtrlCUtc).TotalMilliseconds <= DoubleCopyWindowMs)
                        {
                            _lastCtrlCUtc = DateTime.MinValue;
                            Logger.Info("DoubleCopy detected.");
                            try { DoubleCopy?.Invoke(this, EventArgs.Empty); } catch { }
                        }
                        else
                        {
                            _lastCtrlCUtc = now;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in GlobalHotkeyService.HookCallback.", ex);
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ================= Win32 =================

        private const int WH_KEYBOARD_LL = 13;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // VKs
        private const int VK_MENU = 0x12;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_C = 0x43;

        // WH_KEYBOARD_LL flags
        private const int LLKHF_INJECTED = 0x10;
        private const int LLKHF_LOWER_IL_INJECTED = 0x02;
        private const int LLKHF_ALTDOWN = 0x20;

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
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    }
}
