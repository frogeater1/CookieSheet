using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using CookieSheet.Helpers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CookieSheet
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static Window CurrentWindow { get; private set; }
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.UnhandledException += App_UnhandledException;

            try
            {
                InitializeComponent();
                // 初始化 DebugConsole 的 DispatcherQueue
                DebugConsole.MainDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                DebugConsole.Log("App Started");
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                throw; // Re-throw to let the process terminate, but log first
            }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            // Optional: e.Handled = true; if you want to try to keep running, but usually safe to crash here.
        }

        private void LogCrash(Exception ex)
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var logPath = System.IO.Path.Combine(desktopPath, "CookieSheet_CrashLog.txt");
                var message = $"[{DateTime.Now}] Crash Report:\n{ex}\n--------------------------------------------------\n";
                File.AppendAllText(logPath, message);
            }
            catch
            {
                // Last resort: fails silently if we can't write to desktop
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                DebugConsole.Log("App.OnLaunched started.");
                await ApiService.WarmUpCacheAsync();
                DebugConsole.Log("App.OnLaunched cache warmup finished.");
                _window = new MainWindow();
                CurrentWindow = _window;
                if (_window.Content is FrameworkElement fe)
                {
                    fe.KeyDown += (s, e) =>
                    {
                        if (e.Key == Windows.System.VirtualKey.F12)
                        {
                            DebugConsole.Toggle();
                        }
                    };
                }

                _window.Activate();
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                throw;
            }
        }
    }
}
