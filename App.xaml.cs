using System;
using System.Threading.Tasks;
using System.Windows;
using VisualBuffer.BubbleFX.UI.Bubble;
using VisualBuffer.BubbleFX.UI.Insert;
using VisualBuffer.Diagnostics;
using VisualBuffer.Services.Keyboard;

namespace VisualBuffer
{
    public partial class App : Application
    {
        // Холст можно оставить, если он тебе ещё нужен для другого UI
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

            _clipboard = new Services.Clipboard.ClipboardListener();

            _hotkeys = GlobalHotkeyService.Instance;
            _hotkeys.Install();

            // Пузырь по Ctrl+C×2
            _hotkeys.DoubleCopy += (_, __) =>
            {
                try
                {
                    var last = Services.Clipboard.ClipboardCache.Instance.LastText;
                    if (!string.IsNullOrEmpty(last))
                        Dispatcher.BeginInvoke(() => BubbleFX.UI.Bubble.BubbleManager.Instance.Show(last!));
                }
                catch (Exception ex) { Logger.Error("DoubleCopy handler failed.", ex); }
            };

            // Холст по удержанию Ctrl+Alt
            _hotkeys.CanvasShow += (_, ctx) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        // чтобы вставка с карточки вернулась в исходное окно
                        InsertEngine.Instance.BeginDeferredPaste(ctx.ForegroundHwnd);
                        _canvas!.ShowOverlayHold();
                    });
                }
                catch (Exception ex) { Logger.Error("CanvasShow handler failed.", ex); }
            };

            _hotkeys.CanvasHide += (_, __) =>
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        InsertEngine.Instance.CancelDeferredPaste();
                        _canvas!.Hide();
                    });
                }
                catch (Exception ex) { Logger.Error("CanvasHide handler failed.", ex); }
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
                ex.Handled = true;
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
