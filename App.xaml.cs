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
        private Services.Clipboard.ClipboardListener? _clipboard;
        private BubbleFX.UI.Canvas.CanvasWindow? _canvas;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Logger.Init();
            HookUnhandled();

            _canvas = new BubbleFX.UI.Canvas.CanvasWindow();
            _canvas.Hide();

            // слушатель буфера — для кэша
            _clipboard = new Services.Clipboard.ClipboardListener();

            // НОВОЕ: синглтон + Install(), без new GlobalHotkeyService()
            GlobalHotkeyService.Instance.Install();

            // Пузырь по двойному Ctrl+C (в UI-поток)
            GlobalHotkeyService.Instance.DoubleCopy += (_, __) =>
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
                catch (Exception ex)
                {
                    Logger.Error("DoubleCopy handler failed.", ex);
                }
            };

            Logger.Info("App Startup completed.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { GlobalHotkeyService.Instance.Dispose(); } catch { }
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
