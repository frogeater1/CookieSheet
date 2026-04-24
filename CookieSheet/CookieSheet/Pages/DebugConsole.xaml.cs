using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Microsoft.UI.Dispatching;

namespace CookieSheet
{
    public sealed partial class DebugConsole : Window
    {
        private static DebugConsole? _instance;
        
        // 如果在非 UI 线程调用时调度回主线程
        public static DispatcherQueue? MainDispatcherQueue { get; set; }

        public DebugConsole()
        {
            this.InitializeComponent();
            this.Closed += (s, e) => _instance = null;
        }

        public static void Toggle()
        {
             if (_instance == null)
             {
                 _instance = new DebugConsole();
                 _instance.Activate();
             }
             else
             {
                 _instance.Close();
                 _instance = null;
             }
        }

        public static void Log(object message)
        {
            // 如果窗口没打开，直接丢弃日志
            if (_instance == null) return;

            var logEntry = $"[{DateTime.Now:HH:mm:ss}] {message}\n";

            // 如果窗口开着，同步更新 UI
            if (MainDispatcherQueue == null)
            {
                 try { MainDispatcherQueue = DispatcherQueue.GetForCurrentThread(); } catch { }
            }

            if (MainDispatcherQueue != null)
            {
                MainDispatcherQueue.TryEnqueue(() =>
                {
                    // 只有当实例存在（窗口打开）时才更新 UI
                    if (_instance != null)
                    {
                        _instance.LogOutput.Text += logEntry;
                        _instance.LogOutput.Select(_instance.LogOutput.Text.Length, 0);
                    }
                });
            }
        }
    }
}
