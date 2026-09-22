using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;

namespace CookieSheet
{
    public sealed partial class DebugConsole : Window
    {
        private static DebugConsole? _instance;

        public static DispatcherQueue? MainDispatcherQueue { get; set; }

        public DebugConsole()
        {
            InitializeComponent();
            Closed += (s, e) => _instance = null;
        }

        public static void Toggle()
        {
            if (_instance == null)
            {
                _instance = new DebugConsole();
                _instance.Activate();
                return;
            }

            _instance.Close();
            _instance = null;
        }

        public static void Log(object message)
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            var consoleEntry = logEntry.TrimEnd();

            try
            {
                Console.WriteLine(consoleEntry);
            }
            catch
            {
                Debug.WriteLine(consoleEntry);
            }

            if (MainDispatcherQueue == null)
            {
                try
                {
                    MainDispatcherQueue = DispatcherQueue.GetForCurrentThread();
                }
                catch
                {
                }
            }

            if (_instance == null || MainDispatcherQueue == null)
            {
                return;
            }

            MainDispatcherQueue.TryEnqueue(() =>
            {
                if (_instance == null)
                {
                    return;
                }

                _instance.LogOutput.Text += logEntry;
                _instance.LogOutput.Select(_instance.LogOutput.Text.Length, 0);
            });
        }
    }
}
