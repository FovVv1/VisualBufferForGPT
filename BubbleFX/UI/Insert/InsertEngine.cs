// File: BubbleFX/UI/Insert/InsertEngine.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.BubbleFX.UI.Insert
{
    /// <summary>Единый движок «вставки одной попыткой» с подробным логированием транзакции.
    /// Обновлено: для WPF/Qt-окон используем WM_PASTE в окно с фокусом; для браузеров — Ctrl+V.
    /// Для классических Edit/RichEdit вставка идёт напрямую без клавиш.</summary>
    public sealed class InsertEngine
    {
        public static InsertEngine Instance { get; } = new();

        // --- State for deferred path (не используется в bubble-flow сейчас)
        private nint _deferredTarget;

        public void BeginDeferredPaste(nint hwnd) => _deferredTarget = hwnd;
        public void CancelDeferredPaste() => _deferredTarget = nint.Zero;
        public bool HasDeferredTarget => _deferredTarget != nint.Zero;

        public void PasteToDeferredTarget(string text)
        {
            if (_deferredTarget == nint.Zero) { PasteToForeground(text); return; }
            PasteToHwnd(_deferredTarget, text, null);
            _deferredTarget = nint.Zero;
        }

        // ====================== PUBLIC API ======================

        /// <summary>Главный путь: вставить текст под курсором. «Одна попытка», без многократных дублей.</summary>
        public void PasteAtCursorFocus(string text)
        {
            var txn = new PasteTxn();

            try
            {
                if (!GetCursorPos(out var pt))
                {
                    txn.Log("Cursor", "GetCursorPos failed, fallback to foreground.");
                    PasteToForeground(text, txn);
                    return;
                }
                txn.Cursor = pt;

                var hwndPoint = WindowFromPoint(pt);
                var hwndTop = GetAncestor(hwndPoint, GA_ROOT);
                if (hwndTop == nint.Zero) hwndTop = hwndPoint;

                txn.HwndPoint = hwndPoint;
                txn.HwndTop = hwndTop;
                txn.ClassTop = GetClassNameOf(hwndTop);
                txn.ProcName = GetProcessNameOfWindow(hwndTop);

                txn.HwndDeep = HwndAtScreenPointDeep(hwndTop, pt.X, pt.Y);
                txn.ClassDeep = GetClassNameOf(txn.HwndDeep);

                txn.LogHeader();

                // 0) Подготовим буфер
                SafeSetClipboard(text);
                txn.Log("Clipboard", "Text set (Unicode).");

                // 1) Активируем окно
                BringToForegroundWithLog(hwndTop, txn);

                // 2) Клик по точке (проставим каретку)
                ClickAt(pt.X, pt.Y, txn);
                Thread.Sleep(120);

                // 3) Узнаем, что реально в фокусе после клика
                txn.FocusedAfter = TryGetFocusedWithAttach(hwndTop);
                txn.FocusedAfterClass = GetClassNameOf(txn.FocusedAfter);
                txn.Log("Focus", $"After click focus=0x{txn.FocusedAfter.ToInt64():X} ({txn.FocusedAfterClass})");

                // 4) Скинем модификаторы
                EnsureModifiersReleased(txn);

                // 5) Выбор единственного способа
                if (IsClassicEdit(txn.HwndDeep) || IsClassicEdit(txn.FocusedAfter))
                {
                    PasteToClassicEdit(txn.HwndDeep != nint.Zero ? txn.HwndDeep : txn.FocusedAfter, text, txn);
                    return;
                }

                // Спец-случай: Visual Studio — только Ctrl+V (WM_PASTE может блокироваться UIPI)
                if (IsVisualStudioProcess(txn.ProcName))
                {
                    SafeSetClipboard(text);
                    Thread.Sleep(5);
                    SendCtrlV_Input(35, txn);
                    txn.Log("Result", "Single Ctrl+V sent for Visual Studio (holdMs=35).");
                    return;
                }

                // Для WPF/Qt пробуем WM_PASTE именно в фокусированный HWND
                if (IsWpfClass(txn.ClassTop) || IsQtClass(txn.ClassTop) || IsQtClass(txn.FocusedAfterClass))
                {
                    if (TryWmPasteFocusedOnly(text, hwndTop, txn))
                    {
                        txn.Log("Result", "WM_PASTE to focused sent (WPF/Qt).");
                        return;
                    }
                    // если не удалось получить focused — запасной вариант Ctrl+V ниже
                }

                // Для браузеров/прочих — ровно один Ctrl+V
                SafeSetClipboard(text); // положим ещё раз непосредственно перед клавишами
                Thread.Sleep(10);
                SendCtrlV_Input(0, txn);
                txn.Log("Result", "Single Ctrl+V sent (holdMs=0).");
            }
            catch (Exception ex)
            {
                txn.Log("Error", $"Unhandled: {ex.Message}");
                Logger.Error("InsertEngine: PasteAtCursorFocus failed.", ex);
            }
            finally
            {
                txn.Flush();
            }
        }

        public void PasteToForeground(string text) => PasteToForeground(text, null);

        // ====================== INTERNAL CORE ======================

        private void PasteToForeground(string text, PasteTxn? txn)
        {
            var hwnd = GetForegroundWindow();
            txn?.Log("FG", $"GetForegroundWindow=0x{hwnd.ToInt64():X}");
            if (hwnd == nint.Zero) { SafeSetClipboard(text); return; }
            PasteToHwnd(hwnd, text, txn);
        }

        private void PasteToHwnd(nint hwnd, string text, PasteTxn? txn)
        {
            SafeSetClipboard(text);
            var top = GetAncestor(hwnd, GA_ROOT);
            BringToForegroundWithLog(top, txn);
            Thread.Sleep(80);
            SendCtrlV_Input(0, txn);
            txn?.Log("Result", "Single Ctrl+V to HWND.");
        }

        // ====================== FOCUS/ACTIVATE ======================

        private static void BringToForegroundWithLog(nint hwndTop, PasteTxn? txn)
        {
            if (hwndTop == nint.Zero) { txn?.Log("Activate", "hwndTop=0"); return; }

            if (IsIconic(hwndTop))
            {
                ShowWindow(hwndTop, SW_RESTORE);
                txn?.Log("Activate", "SW_RESTORE sent.");
            }

            var fg = GetForegroundWindow();
            uint targetThread = GetWindowThreadProcessId(hwndTop, out _);
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint curThread = GetCurrentThreadId();

            bool ok = true;
            try
            {
                AttachThreadInput(curThread, fgThread, true);
                AttachThreadInput(curThread, targetThread, true);

                if (!SetForegroundWindow(hwndTop)) ok = false;
                SetFocus(hwndTop);
            }
            finally
            {
                AttachThreadInput(curThread, targetThread, false);
                AttachThreadInput(curThread, fgThread, false);
            }

            txn?.Log("Activate", $"BringToForeground hwnd=0x{hwndTop.ToInt64():X}, ok={ok}");
        }

        private static nint TryGetFocusedWithAttach(nint hwndTop)
        {
            if (hwndTop == nint.Zero) return nint.Zero;

            uint targetThread = GetWindowThreadProcessId(hwndTop, out _);
            uint curThread = GetCurrentThreadId();

            nint focused = nint.Zero;
            try
            {
                AttachThreadInput(curThread, targetThread, true);
                focused = GetFocus();
            }
            finally
            {
                AttachThreadInput(curThread, targetThread, false);
            }
            return focused;
        }

        private static bool TryWmPasteFocusedOnly(string text, nint hwndTop, PasteTxn? txn)
        {
            try
            {
                uint t = GetWindowThreadProcessId(hwndTop, out _);
                uint cur = GetCurrentThreadId();
                nint focused = nint.Zero;

                AttachThreadInput(cur, t, true);
                try { focused = GetFocus(); }
                finally { AttachThreadInput(cur, t, false); }

                if (focused == nint.Zero)
                {
                    txn?.Log("WM_PASTE", "No focused HWND.");
                    return false;
                }

                SafeSetClipboard(text);
                SendMessage(focused, WM_PASTE, nint.Zero, nint.Zero);
                txn?.Log("WM_PASTE", $"Sent to focused=0x{focused.ToInt64():X}");
                return true;
            }
            catch (Exception ex)
            {
                txn?.Log("WM_PASTE", $"Failed: {ex.Message}");
                return false;
            }
        }

        private static void ClickAt(int x, int y, PasteTxn? txn)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) { txn?.Log("Click", "Virtual screen size invalid."); return; }

            int ax = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));

            var inputs = new INPUT[3];
            inputs[0] = MouseAbs(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);
            inputs[1] = MouseAbs(ax, ay, MOUSEEVENTF_LD | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);
            inputs[2] = MouseAbs(ax, ay, MOUSEEVENTF_LU | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Click", $"ClickAt ({x},{y}) abs=({ax},{ay}) sent={sent}");
        }

        // ====================== INSERT METHODS ======================

        private static void PasteToClassicEdit(nint hwndEdit, string text, PasteTxn? txn)
        {
            if (hwndEdit == nint.Zero)
            {
                txn?.Log("ClassicEdit", "hwnd=0, bail to Ctrl+V");
                return;
            }

            try
            {
                SafeSetClipboard(text);
                SendMessage(hwndEdit, WM_PASTE, nint.Zero, nint.Zero);
                // Доп. страховка для Win32 Edit/RichEdit
                //SendMessage(hwndEdit, EM_REPLACESEL, new nint(1), text);
                txn?.Log("Result", $"ClassicEdit paste to 0x{hwndEdit.ToInt64():X}");
            }
            catch (Exception ex)
            {
                txn?.Log("ClassicEdit", $"Exception: {ex.Message}");
            }
        }

        private static void SendCtrlV_Input(int holdMs, PasteTxn? txn)
        {
            var downs = new[] { KeyDown(VK_CONTROL), KeyDown(VK_V) };
            var s1 = SendInput((uint)downs.Length, downs, Marshal.SizeOf(typeof(INPUT)));
            if (holdMs > 0) Thread.Sleep(holdMs);
            var ups = new[] { KeyUp(VK_V), KeyUp(VK_CONTROL) };
            var s2 = SendInput((uint)ups.Length, ups, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Keys", $"Ctrl+V: down={s1}, up={s2}, holdMs={holdMs}");
        }

        private static void EnsureModifiersReleased(PasteTxn? txn)
        {
            var ups = new[]
            {
                KeyUp(VK_CONTROL),
                KeyUp(VK_SHIFT),
                KeyUp(VK_MENU)
            };
            var s = SendInput((uint)ups.Length, ups, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Keys", $"EnsureModifiersReleased sentUp={s}");
        }

        // ====================== DETECTION HELPERS ======================

        private static bool IsClassicEdit(nint hwnd)
        {
            if (hwnd == nint.Zero) return false;
            var cls = GetClassNameOf(hwnd);
            if (string.IsNullOrEmpty(cls)) return false;
            return cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                   cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWpfClass(string cls) =>
            !string.IsNullOrEmpty(cls) && cls.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase);

        private static bool IsQtClass(string cls) =>
            !string.IsNullOrEmpty(cls) && cls.StartsWith("Qt", StringComparison.OrdinalIgnoreCase);

        private static bool IsVisualStudioProcess(string proc) =>
            !string.IsNullOrEmpty(proc) && proc.Equals("devenv", StringComparison.OrdinalIgnoreCase);

        private static nint HwndAtScreenPointDeep(nint hwndTop, int x, int y)
        {
            var pt = new POINT { X = x, Y = y };
            var first = WindowFromPoint(pt);
            if (first == nint.Zero) return nint.Zero;

            var relative = pt;
            if (!ScreenToClient(first, ref relative)) return first;

            var deep = ChildWindowFromPointEx(first, relative, CWP_SKIPDISABLED | CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
            return deep != nint.Zero ? deep : first;
        }

        private static string GetClassNameOf(nint hwnd)
        {
            if (hwnd == nint.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) != 0)
                return sb.ToString();
            return string.Empty;
        }

        private static string GetProcessNameOfWindow(nint hwndTop)
        {
            try
            {
                GetWindowThreadProcessId(hwndTop, out var pid);
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        // ====================== Clipboard ======================

        private static void SafeSetClipboard(string text)
        {
            try { Clipboard.SetText(text); }
            catch
            {
                Thread.Sleep(20);
                Clipboard.SetText(text);
            }
        }

        // ====================== Win32 ======================

        private const int WM_PASTE = 0x0302;
        private const int EM_REPLACESEL = 0x00C2;

        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_V = 0x56;

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABS = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_LD = 0x0002;
        private const uint MOUSEEVENTF_LU = 0x0004;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private const uint GA_ROOT = 2;
        private const uint CWP_SKIPINVISIBLE = 0x0001;
        private const uint CWP_SKIPDISABLED = 0x0002;
        private const uint CWP_SKIPTRANSPARENT = 0x0004;

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")] static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] static extern nint SetFocus(nint hWnd);
        [DllImport("user32.dll")] static extern nint WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] static extern bool IsIconic(nint hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint gaFlags);
        [DllImport("user32.dll")] static extern nint GetFocus();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);
        [DllImport("user32.dll")] static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern nint ChildWindowFromPointEx(nint hwndParent, POINT pt, uint flags);

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public InputUnion U; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        private static INPUT MouseAbs(int ax, int ay, uint flags) => new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags, mouseData = 0, time = 0, dwExtraInfo = nint.Zero }
            }
        };
        private static INPUT KeyDown(int vk) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = nint.Zero } }
        };
        private static INPUT KeyUp(int vk) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = nint.Zero } }
        };

        // ====================== TELEMETРИЯ ======================

        private sealed class PasteTxn
        {
            private readonly StringBuilder _sb = new();
            private readonly string _id = Guid.NewGuid().ToString("N").Substring(0, 8);

            public POINT Cursor;
            public nint HwndPoint, HwndTop, HwndDeep;
            public string ClassTop = "", ClassDeep = "", ProcName = "";
            public nint FocusedAfter;
            public string FocusedAfterClass = "";

            public void LogHeader()
            {
                _sb.AppendLine($"InsertEngine[T#{_id}]: START pt=({Cursor.X},{Cursor.Y})");
                _sb.AppendLine($"InsertEngine[T#{_id}]: Top=0x{HwndTop.ToInt64():X} clsTop='{ClassTop}' proc='{ProcName}'");
                _sb.AppendLine($"InsertEngine[T#{_id}]: Point=0x{HwndPoint.ToInt64():X}  Deep=0x{HwndDeep.ToInt64():X} clsDeep='{ClassDeep}'");
            }

            public void Log(string tag, string msg)
            {
                _sb.AppendLine($"InsertEngine[T#{_id}] {tag}: {msg}");
            }

            public void Flush()
            {
                _sb.AppendLine($"InsertEngine[T#{_id}]: END");
                Logger.Info(_sb.ToString().TrimEnd());
            }
        }
    }
}
