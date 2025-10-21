using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using VisualBuffer.Diagnostics;

namespace VisualBuffer.BubbleFX.UI.Insert
{
    /// <summary>
    /// Движок «одной попытки»: либо Ctrl+V (scan), либо WM_PASTE (только Classic Edit).
    /// Надёжная установка буфера обмена с Win32-ретраями (без WPF Clipboard).
    /// </summary>
    public sealed class InsertEngine
    {
        public static InsertEngine Instance { get; } = new();

        // --- deferred (на будущее, не используется в bubble-flow сейчас)
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

        // =================== PUBLIC ===================

        public void PasteAtCursorFocus(string text)
        {
            var txn = new PasteTxn();
            try
            {
                if (!GetCursorPos(out var pt))
                {
                    txn.Log("Cursor", "GetCursorPos failed -> foreground path");
                    PasteToForeground(text, txn);
                    return;
                }
                txn.Cursor = pt;

                // Что под курсором
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

                // 1) Активируем окно и кликом ставим каретку
                BringToForegroundWithLog(hwndTop, txn);
                ClickAt(pt.X, pt.Y, txn);
                Thread.Sleep(160); // дать фокусу стабилизироваться

                // 2) Убеждаемся, что активен нужный top-level; читаем фактический фокус
                EnsureTargetForeground(hwndTop, txn);

                // 3) Модификаторы «на всякий» отпускаем
                EnsureModifiersReleased(txn);

                // 4) Выбор РОВНО одного способа: Classic Edit -> WM_PASTE, иначе Ctrl+V(scan)
                if (IsClassicEdit(txn.HwndDeep) || IsClassicEdit(txn.FocusedAfter))
                {
                    // Буфер — перед самой вставкой. Если не смогли установить — выходим без клавиш.
                    if (!SafeSetClipboard(text, txn))
                    {
                        txn.Log("Clipboard", "Set failed -> cancel WM_PASTE");
                        return;
                    }
                    try
                    {
                        SendMessage(txn.HwndDeep != nint.Zero ? txn.HwndDeep : txn.FocusedAfter, WM_PASTE, nint.Zero, nint.Zero);
                        txn.Log("Result", "WM_PASTE sent to Classic Edit/RichEdit");
                    }
                    catch (Exception ex)
                    {
                        txn.Log("WM_PASTE", $"Exception: {ex.Message}");
                    }
                    return;
                }

                // Все прочие окна — только Ctrl+V (scan). Для VS/WPF — удержание 35 мс.
                int holdMs = (IsVisualStudioProcess(txn.ProcName) || IsWpfClass(txn.ClassTop) || IsWpfClass(txn.FocusedAfterClass)) ? 35 : 0;

                if (!SafeSetClipboard(text, txn))
                {
                    txn.Log("Clipboard", "Set failed -> cancel Ctrl+V");
                    return;
                }

                Thread.Sleep(10);
                EnsureModifiersReleased(txn);
                SendCtrlV_Scan(holdMs, txn);
                txn.Log("Result", holdMs > 0 ? "Ctrl+V(scan) with hold" : "Ctrl+V(scan)");
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

        // =================== INTERNAL CORE ===================

        private void PasteToForeground(string text, PasteTxn? txn)
        {
            var hwnd = GetForegroundWindow();
            txn?.Log("FG", $"GetForegroundWindow=0x{hwnd.ToInt64():X}");
            if (hwnd == nint.Zero) { SafeSetClipboard(text, txn); return; }
            PasteToHwnd(hwnd, text, txn);
        }

        private void PasteToHwnd(nint hwnd, string text, PasteTxn? txn)
        {
            var top = GetAncestor(hwnd, GA_ROOT);
            BringToForegroundWithLog(top, txn);
            Thread.Sleep(80);
            EnsureTargetForeground(top, txn);

            // Здесь, как и в основном пути, — ТОЛЬКО Ctrl+V(scan)
            if (!SafeSetClipboard(text, txn))
            {
                txn?.Log("Clipboard", "Set failed -> cancel Ctrl+V to HWND");
                return;
            }
            Thread.Sleep(10);
            EnsureModifiersReleased(txn);
            SendCtrlV_Scan(0, txn);
            txn?.Log("Result", "Ctrl+V(scan) to HWND");
        }

        // =================== ACTIVATE/FOCUS ===================

        private static void BringToForegroundWithLog(nint hwndTop, PasteTxn? txn)
        {
            if (hwndTop == nint.Zero) { txn?.Log("Activate", "hwndTop=0"); return; }

            if (IsIconic(hwndTop))
            {
                ShowWindow(hwndTop, SW_RESTORE);
                txn?.Log("Activate", "SW_RESTORE");
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

        private static void EnsureTargetForeground(nint hwndTop, PasteTxn? txn)
        {
            var fg = GetForegroundWindow();
            if (GetAncestor(fg, GA_ROOT) != hwndTop)
            {
                txn?.Log("Focus", $"FG is different (0x{fg.ToInt64():X}) -> re-activate");
                BringToForegroundWithLog(hwndTop, txn);
                Thread.Sleep(20);
            }

            txn!.FocusedAfter = TryGetFocusedWithAttach(hwndTop);
            txn.FocusedAfterClass = GetClassNameOf(txn.FocusedAfter);
            txn.Log("Focus", $"After ensure FG: focus=0x{txn.FocusedAfter.ToInt64():X} ({txn.FocusedAfterClass})");
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

        private static void ClickAt(int x, int y, PasteTxn? txn)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) { txn?.Log("Click", "Virtual screen invalid"); return; }

            int ax = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));

            var inputs = new INPUT[3];
            inputs[0] = MouseAbs(ax, ay, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);
            inputs[1] = MouseAbs(ax, ay, MOUSEEVENTF_LD | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);
            inputs[2] = MouseAbs(ax, ay, MOUSEEVENTF_LU | MOUSEEVENTF_ABS | MOUSEEVENTF_VIRTUALDESK);

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Click", $"ClickAt ({x},{y}) abs=({ax},{ay}) sent={sent}");
        }

        // =================== INSERT ===================

        private static void SendCtrlV_Scan(int holdMs, PasteTxn? txn)
        {
            var downs = new[] { KeyDownScan(VK_CONTROL), KeyDownScan(VK_V) };
            var d = SendInput((uint)downs.Length, downs, Marshal.SizeOf(typeof(INPUT)));
            if (holdMs > 0) Thread.Sleep(holdMs);
            var ups = new[] { KeyUpScan(VK_V), KeyUpScan(VK_CONTROL) };
            var u = SendInput((uint)ups.Length, ups, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Keys", $"Ctrl+V[scan]: down={d}, up={u}, holdMs={holdMs}");
        }

        private static void EnsureModifiersReleased(PasteTxn? txn)
        {
            var ups = new[] { KeyUp(VK_CONTROL), KeyUp(VK_SHIFT), KeyUp(VK_MENU) };
            var s = SendInput((uint)ups.Length, ups, Marshal.SizeOf(typeof(INPUT)));
            txn?.Log("Keys", $"EnsureModifiersReleased sentUp={s}");
        }

        private static void PasteToClassicEdit(nint hwndEdit, string text, PasteTxn? txn)
        {
            if (hwndEdit == nint.Zero) { txn?.Log("ClassicEdit", "hwnd=0"); return; }
            if (!SafeSetClipboard(text, txn))
            {
                txn?.Log("Clipboard", "Set failed -> cancel WM_PASTE");
                return;
            }
            SendMessage(hwndEdit, WM_PASTE, nint.Zero, nint.Zero);
            txn?.Log("Result", $"WM_PASTE -> 0x{hwndEdit.ToInt64():X}");
        }

        // =================== DETECTION ===================

        private static bool IsClassicEdit(nint hwnd)
        {
            if (hwnd == nint.Zero) return false;
            var cls = GetClassNameOf(hwnd);
            return !string.IsNullOrEmpty(cls) &&
                   (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                    cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsWpfClass(string cls) =>
            !string.IsNullOrEmpty(cls) && cls.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase);

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
            if (GetClassName(hwnd, sb, sb.Capacity) != 0) return sb.ToString();
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

        // =================== Clipboard (надёжно) ===================

        /// <summary>
        /// Устанавливает Unicode-текст в буфер через Win32 с ретраями.
        /// Безопасно для любых потоков. Возвращает true при успехе.
        /// </summary>
        private static bool SafeSetClipboard(string text, PasteTxn? txn)
        {
            // Добавим завершающий '\0'
            var data = text + "\0";
            int bytes = Encoding.Unicode.GetByteCount(data);

            // Пытаемся открыть буфер обмена до 20 раз (~400мс)
            for (int i = 0; i < 20; i++)
            {
                if (OpenClipboard(nint.Zero))
                {
                    IntPtr hGlobal = IntPtr.Zero;
                    try
                    {
                        hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)bytes);
                        if (hGlobal == IntPtr.Zero) { txn?.Log("Clipboard", "GlobalAlloc failed"); CloseClipboard(); return false; }

                        IntPtr p = GlobalLock(hGlobal);
                        if (p == IntPtr.Zero) { txn?.Log("Clipboard", "GlobalLock failed"); GlobalFree(hGlobal); CloseClipboard(); return false; }

                        try
                        {
                            // Пишем UTF-16LE
                            var buf = Encoding.Unicode.GetBytes(data);
                            Marshal.Copy(buf, 0, p, buf.Length);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }

                        EmptyClipboard();
                        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                        {
                            // В случае ошибки владеем памятью — освобождаем
                            txn?.Log("Clipboard", $"SetClipboardData failed, lasterror={Marshal.GetLastWin32Error()}");
                            GlobalFree(hGlobal);
                            CloseClipboard();
                            return false;
                        }

                        // Успех: после SetClipboardData память переходит системе — НЕ освобождать!
                        CloseClipboard();
                        txn?.Log("Clipboard", "Text set (Win32)");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        // Если не успели отдать память системе — освободим
                        if (hGlobal != IntPtr.Zero)
                        {
                            try { GlobalFree(hGlobal); } catch { }
                        }
                        CloseClipboard();
                        txn?.Log("Clipboard", $"Exception: {ex.Message}");
                        return false;
                    }
                }

                // Не открыли — подождём и попробуем снова
                Thread.Sleep(20);
            }

            txn?.Log("Clipboard", "OpenClipboard timeout");
            return false;
        }

        // =================== Win32 ===================

        private const int WM_PASTE = 0x0302;

        // Keys
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12; // Alt
        private const int VK_V = 0x56;

        // Input
        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABS = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_LD = 0x0002;
        private const uint MOUSEEVENTF_LU = 0x0004;

        // Screen
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        // Ancestor / Child search
        private const uint GA_ROOT = 2;
        private const uint CWP_SKIPINVISIBLE = 0x0001;
        private const uint CWP_SKIPDISABLED = 0x0002;
        private const uint CWP_SKIPTRANSPARENT = 0x0004;

        private const int SW_RESTORE = 9;

        // Clipboard
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;

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
        [DllImport("user32.dll")] static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] static extern nint ChildWindowFromPointEx(nint hwndParent, POINT pt, uint flags);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern nint SendMessage(nint hWnd, int Msg, nint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr hMem);

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
            U = new InputUnion { mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = flags } }
        };

        private static INPUT KeyDown(int vk) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = 0, dwFlags = 0 } }
        };
        private static INPUT KeyUp(int vk) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP } }
        };

        private static INPUT KeyDownScan(int vk)
        {
            ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
            uint flags = KEYEVENTF_SCANCODE;
            if (vk == VK_CONTROL || vk == VK_MENU) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags } } };
        }

        private static INPUT KeyUpScan(int vk)
        {
            ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
            uint flags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
            if (vk == VK_CONTROL || vk == VK_MENU) flags |= KEYEVENTF_EXTENDEDKEY;
            return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = flags } } };
        }

        // =================== TELEMETRY ===================

        private sealed class PasteTxn
        {
            private readonly StringBuilder _sb = new();
            private readonly string _id = Guid.NewGuid().ToString("N")[..8];

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

            public void Log(string tag, string msg) => _sb.AppendLine($"InsertEngine[T#{_id}] {tag}: {msg}");

            public void Flush()
            {
                _sb.AppendLine($"InsertEngine[T#{_id}]: END");
                Logger.Info(_sb.ToString().TrimEnd());
            }
        }
    }
}