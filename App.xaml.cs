using System;
using System.Threading.Tasks;
using System.Windows;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Insert;
using VisualBuffer.Diagnostics;
using VisualBuffer.Services.Keyboard;

namespace VisualBuffer
{
    public partial class App : System.Windows.Application
    {
        private GlobalHotkeyService? _hotkeys;
        private Services.Clipboard.ClipboardListener? _clipboard;
        private BubbleFX.UI.Canvas.CanvasWindow? _canvas;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Logger.Init();
            HookUnhandled();

            _canvas = new BubbleFX.UI.Canvas.CanvasWindow();
            _canvas.Hide();

            // слушатель буфера — только для кэша, без показа UI
            _clipboard = new Services.Clipboard.ClipboardListener();

            _hotkeys = new GlobalHotkeyService();

            // Пузырь — только по Ctrl+C×2 (в UI-поток!)
            _hotkeys.DoubleCopy += (_, __) =>
            {
                try
                {
                    var last = Services.Clipboard.ClipboardCache.Instance.LastText;
                    if (!string.IsNullOrEmpty(last))
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            BubbleManager.Instance.Show(last!);
                        });
                    }
                }
                catch (Exception ex) { Logger.Error("DoubleCopy handler failed.", ex); }
            };

            // Холст — показываем, пока удерживается второй Ctrl+V
            _hotkeys.PasteHoldStart += (_, ctx) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        InsertEngine.Instance.BeginDeferredPaste(ctx.ForegroundHwnd);
                        _canvas!.ShowOverlayHold();
                    });
                }
                catch (Exception ex) { Logger.Error("PasteHoldStart handler failed.", ex); }
            };

            _hotkeys.PasteHoldEnd += (_, __) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        InsertEngine.Instance.CancelDeferredPaste();
                        _canvas!.Hide();
                    });
                }
                catch (Exception ex) { Logger.Error("PasteHoldEnd handler failed.", ex); }
            };

            Logger.Info("App Startup completed.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkeys?.Dispose();
            _clipboard?.Dispose();
            base.OnExit(e);
            Logger.Info("=== App exit ===");
        }

        private void HookUnhandled()
        {
            this.DispatcherUnhandledException += (s, ex) =>
            {
                Logger.Error("DispatcherUnhandledException", ex.Exception);
                ex.Handled = true; // чтобы приложение не падало
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                Logger.Error("AppDomain.UnhandledException", ex.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Logger.Error("TaskScheduler.UnobservedTaskException", ex.Exception);
                ex.SetObserved();
            };
        }
    }
}
