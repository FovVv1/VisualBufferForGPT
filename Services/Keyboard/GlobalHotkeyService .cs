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

        private readonly Stopwatch _copySw = new();
        private readonly Stopwatch _pasteSw = new();

        private const int CopyDoubleThresholdMs = 1000; // Ctrl+C×2: ~1 сек
        private const int PasteDoubleThresholdMs = 350;  // Ctrl+V×2: быстрый

        // Win32
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_CONTROL = 0x11;
        private const int VK_C = 0x43;
        private const int VK_V = 0x56;

        private bool _ctrlDown;
        private bool _vIsDown;              // ← чтобы отсекать авто-повторы WM_KEYDOWN(V)
        private bool _awaitingPaste;
        private bool _canvasHoldActive;
        private IntPtr _pendingPasteHwnd;

        public GlobalHotkeyService()
        {
            _proc = HookCallback;
            _hookId = SetHook(_proc);
            Logger.Info("GlobalHotkeyService hook installed.");
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Logger.Info("GlobalHotkeyService hook uninstalled.");
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using var p = Process.GetCurrentProcess();
            using var m = p.MainModule!;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(m.ModuleName), 0);
        }

        private static bool IsKey(int vk, int key) => vk == key;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int vkCode = Marshal.ReadInt32(lParam);

                    if (wParam == (IntPtr)WM_KEYDOWN)
                    {
                        // CTRL state
                        if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                            _ctrlDown = true;

                        // Ctrl+C×2 — с порогом 1 сек
                        if (_ctrlDown && IsKey(vkCode, VK_C))
                        {
                            if (_copySw.IsRunning && _copySw.ElapsedMilliseconds <= CopyDoubleThresholdMs)
                            {
                                _copySw.Reset();
                                Logger.Info("DoubleCopy detected.");
                                DoubleCopy?.Invoke(this, EventArgs.Empty);
                            }
                            else
                            {
                                _copySw.Restart();
                            }
                        }

                        // Ctrl+V логика (двойное нажатие → hold до отпускания)
                        if (_ctrlDown && IsKey(vkCode, VK_V))
                        {
                            // отсекаем авто-повторы, пока V держат
                            if (_vIsDown)
                                return (IntPtr)1; // подавляем повторное WM_KEYDOWN(V)

                            _vIsDown = true;

                            // если холст уже в hold — просто подавляем дальнейшие V
                            if (_canvasHoldActive)
                                return (IntPtr)1;

                            if (!_awaitingPaste)
                            {
                                // первое V: ждём второе в пределах порога
                                _awaitingPaste = true;
                                _pasteSw.Restart();
                                _pendingPasteHwnd = GetForegroundWindow();
                                return (IntPtr)1; // подавляем первое V
                            }
                            else if (_pasteSw.ElapsedMilliseconds <= PasteDoubleThresholdMs)
                            {
                                // второе V во времени → запускаем hold
                                _awaitingPaste = false;
                                _pasteSw.Reset();

                                _canvasHoldActive = true;
                                Logger.Info("PasteHoldStart.");
                                PasteHoldStart?.Invoke(this, new PasteHoldContext(_pendingPasteHwnd));
                                return (IntPtr)1;
                            }
                            // если второе V пришло слишком поздно — дадим обработаться блоком WM_KEYUP (ре-инжект)
                        }
                    }
                    else if (wParam == (IntPtr)WM_KEYUP)
                    {
                        // отпускание V
                        if (vkCode == VK_V)
                        {
                            _vIsDown = false;

                            if (_canvasHoldActive)
                            {
                                _canvasHoldActive = false;
                                Logger.Info("PasteHoldEnd.");
                                PasteHoldEnd?.Invoke(this, EventArgs.Empty);
                            }
                            else if (_awaitingPaste && _pasteSw.ElapsedMilliseconds > PasteDoubleThresholdMs)
                            {
                                // было одно V — считаем обычной вставкой
                                _awaitingPaste = false;
                                _pasteSw.Reset();
                                Logger.Info("Single Ctrl+V detected — reinject.");
                                SendCtrlV();
                            }
                        }

                        // отпускание CTRL
                        if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                        {
                            _ctrlDown = false;

                            // если при отпускании Ctrl был активен hold — завершить
                            if (_canvasHoldActive)
                            {
                                _canvasHoldActive = false;
                                Logger.Info("PasteHoldEnd (Ctrl up).");
                                PasteHoldEnd?.Invoke(this, EventArgs.Empty);
                            }

                            // сброс «ожидания второго V», чтобы не залипало состояние
                            if (_awaitingPaste)
                            {
                                _awaitingPaste = false;
                                _pasteSw.Reset();

                                //WW
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in HookCallback.", ex);
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private static void SendCtrlV()
        {
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event((byte)VK_CONTROL, 0, 0, 0);
            keybd_event((byte)VK_V, 0, 0, 0);
            keybd_event((byte)VK_V, 0, KEYEVENTF_KEYUP, 0);
            keybd_event((byte)VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        }

        public sealed record PasteHoldContext(IntPtr ForegroundHwnd);

        // Win32
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);
    }
}
