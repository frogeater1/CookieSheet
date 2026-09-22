using System;
using System.Collections.Generic;
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
using Windows.Foundation;
using Windows.Foundation.Collections;
using MiniExcelLibs;
using System.Linq;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CookieSheet
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            // DebugConsole.Toggle();
            InitializeComponent();
            if (Uri.TryCreate(Environment.GetEnvironmentVariable("COOKIESHEET_API_URL"), UriKind.Absolute, out var api)
                && api.IsLoopback)
                Title += "（本地测试）";
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            // Set initial page
            NavView.SelectedItem = NavView.MenuItems[0];
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                // ContentFrame.Navigate(typeof(SettingsPage));
            }
            else
            {
                var tag = args.SelectedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "Import":
                        ContentFrame.Navigate(typeof(ImportPage));
                        break;
                    case "Query":
                        ContentFrame.Navigate(typeof(QueryPage));
                        break;
                    case "Orders":
                        ContentFrame.Navigate(typeof(OrderListPage));
                        break;
                    case "UnmatchedRefunds":
                        ContentFrame.Navigate(typeof(UnmatchedRefundsPage));
                        break;
                }
            }
        }
    }
}
