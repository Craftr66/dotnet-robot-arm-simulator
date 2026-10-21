using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AxisLab.Core;
using AxisLab.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace AxisLab.Desktop;

internal sealed class MainWindow : Window
{
    private readonly WebView2 webView = new();
    private readonly TextBlock loadingText = new()
    {
        Text = "正在启动 AxisLab…", Foreground = Brushes.LightGray,
        FontSize = 18, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        MaxWidth = 640, Margin = new Thickness(30)
    };
    private readonly CancellationTokenSource lifetime = new();
    private WebApplication? server;
    private string? baseUrl;
    private Task startup = Task.CompletedTask;
    private bool closing;
    private bool allowClose;
    private bool pageReady;

    public MainWindow()
    {
        Title = Program.WindowTitle;
        Width = 1440; Height = 940; MinWidth = 900; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(18, 26, 37));
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/AxisLab;component/Assets/AxisLab.ico"));
        var layout = new DockPanel();
        var menu = MakeMenu(); DockPanel.SetDock(menu, Dock.Top); layout.Children.Add(menu);
        var content = new Grid(); content.Children.Add(loadingText);
        webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 26, 37);
        webView.Visibility = Visibility.Hidden; content.Children.Add(webView);
        layout.Children.Add(content); Content = layout;
        Loaded += (_, _) => startup = InitializeAsync();
        Closing += OnClosing;
        SourceInitialized += (_, _) =>
        {
            var dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
    }

    private Menu MakeMenu()
    {
        var menu = new Menu { Background = new SolidColorBrush(Color.FromRgb(23, 32, 44)), Foreground = Brushes.LightGray,
            Padding = new Thickness(10, 3, 10, 3) };
        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, menu.Background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.LightGray));
        menu.Resources.Add(typeof(MenuItem), style);
        var file = new MenuItem { Header = "文件" };
        file.Items.Add(Item("新建工程", () => ClickPageButton("new")));
        file.Items.Add(Item("打开已保存工程", () => ClickPageButton("open")));
        file.Items.Add(Item("保存工程", () => ClickPageButton("save")));
        file.Items.Add(new Separator());
        file.Items.Add(Item("导入 JSON 工程…", () => ClickPageButton("import")));
        file.Items.Add(Item("导出 JSON 工程…", () => ClickPageButton("export")));
        file.Items.Add(new Separator());
        file.Items.Add(Item("退出", Close));
        menu.Items.Add(file);
        var tools = new MenuItem { Header = "工具" };
        tools.Items.Add(Item("打开工程文件夹", () => OpenFolder(DesktopPaths.Projects)));
        tools.Items.Add(Item("打开示例文件夹", () => OpenFolder(Path.Combine(AppContext.BaseDirectory, "Samples"))));
        tools.Items.Add(Item("打开日志文件夹", () => OpenFolder(DesktopPaths.Home)));
        tools.Items.Add(Item("连接信息", () => MessageBox.Show(this,
            ConnectionInfo(),
            "AxisLab 连接信息", MessageBoxButton.OK, MessageBoxImage.Information)));
        tools.Items.Add(Item("复制 HTTP 连接地址", () => { if (baseUrl is not null) Clipboard.SetText(baseUrl); }));
        tools.Items.Add(Item("复制全部连接信息", () => Clipboard.SetText(ConnectionInfo())));
        menu.Items.Add(tools);
        var help = new MenuItem { Header = "帮助" };
        help.Items.Add(Item("场景操作说明", () => ClickPageButton("help")));
        help.Items.Add(Item("关于 AxisLab", () => MessageBox.Show(this,
            "AxisLab 桌面版 0.7.0\n机械臂与视觉仿真平台\n\n阶段 7B：可识别九点标定板与 TCP 圆点，配合 WPF 九点采样、VisionMaster 平面标定与独立验证。\n标定求解由用户 VM 流程完成。",
            "关于 AxisLab", MessageBoxButton.OK, MessageBoxImage.Information)));
        menu.Items.Add(help);
        return menu;
    }

    private static MenuItem Item(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action(); return item;
    }
    private async void ClickPageButton(string id)
    {
        if (pageReady && !closing)
            await webView.ExecuteScriptAsync($"document.getElementById('{id}')?.click()");
    }
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(DesktopPaths.Projects);
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")))
                throw new FileNotFoundException("缺少界面资源。请保留完整发布文件夹，或重新解压发行包后启动。");
            DesktopPaths.Log("Starting desktop 0.7.0");
            server = SimulationHost.Build([], new(AppContext.BaseDirectory, DesktopPaths.Projects, $"http://{ModbusServer.ListenAddress}:0", Quiet: true));
            await server.StartAsync(lifetime.Token);
            var listenHost = ModbusServer.ListenAddress.ToString();
            baseUrl = new UriBuilder(server.Urls.Single()) { Host = listenHost == "0.0.0.0" ? "127.0.0.1" : listenHost }.Uri.GetLeftPart(UriPartial.Authority);
            DesktopPaths.Log("ServiceReady " + baseUrl);
            loadingText.Text = "正在加载三维场景…";
            // Test automation opts in explicitly; normal releases have no debugging port.
            var testArguments = Environment.GetEnvironmentVariable("AXISLAB_TEST_BROWSER_ARGUMENTS");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: DesktopPaths.WebViewData,
                options: new CoreWebView2EnvironmentOptions(testArguments ?? ""))
                .WaitAsync(lifetime.Token);
            await webView.EnsureCoreWebView2Async(environment).WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var core = webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = !string.IsNullOrWhiteSpace(testArguments);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.GetLeftPart(UriPartial.Authority) != baseUrl)
                {
                    DesktopPaths.Log("NavigationBlocked " + (target?.Scheme ?? "invalid"));
                    e.Cancel = true;
                }
            };
            core.DownloadStarting += OnDownloadStarting;
            core.ProcessFailed += (_, e) =>
            {
                DesktopPaths.Log("WebView process failure: " + e.ProcessFailedKind);
                MessageBox.Show(this, "三维界面进程已停止，请关闭并重新启动 AxisLab。已保存工程不受影响。",
                    "AxisLab", MessageBoxButton.OK, MessageBoxImage.Error);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    pageReady = true; webView.Visibility = Visibility.Visible;
                    loadingText.Visibility = Visibility.Collapsed;
                    DesktopPaths.Log("DesktopReady " + baseUrl);
                }
                else if (!closing)
                {
                    DesktopPaths.Log("Navigation failed: " + e.WebErrorStatus);
                    loadingText.Text = "界面加载失败，请重启程序。\n错误：" + e.WebErrorStatus;
                }
            };
            core.Navigate(baseUrl + "/");
        }
        catch (OperationCanceledException) { }
        catch (WebView2RuntimeNotFoundException ex)
        {
            DesktopPaths.Log(ex.ToString());
            loadingText.Text = "此电脑缺少 Microsoft Edge WebView2 Runtime。\n\n请安装 Microsoft 官方 Evergreen WebView2 Runtime（x64），然后重新启动 AxisLab。\n下载地址：https://developer.microsoft.com/microsoft-edge/webview2/";
        }
        catch (Exception ex)
        {
            DesktopPaths.Log(ex.ToString());
            loadingText.Text = "AxisLab 启动失败\n\n" + ex.Message + "\n\n诊断日志：" + Path.Combine(DesktopPaths.Home, "desktop.log");
        }
    }

    private string ConnectionInfo()
    {
        var state = server?.Services.GetRequiredService<CommunicationService>().Status();
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : "127.0.0.1";
        return $"HTTP：{baseUrl ?? "尚未启动"}\nModbus TCP：{host}:{state?.ModbusPort}，Unit ID 1\n" +
            $"监听地址：{state?.BindAddress}\n状态：{(state?.Listening == true ? "正在监听" : state?.Error ?? "尚未启动")}\n工程目录：{DesktopPaths.Projects}\n\n" +
            "本机程序可直接使用上述地址。其他电脑需启用局域网监听并使用本机 IP。\n详细协议和连接示例见程序目录的外部连接快速开始与 Samples/ModbusClient。";
    }
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var name = Path.GetFileName(e.ResultFilePath);
            DesktopPaths.Log("DownloadStarting " + name);
            var dialog = new SaveFileDialog
            {
                Title = "导出文件", FileName = string.IsNullOrWhiteSpace(name) ? "工程.scene.json" : name,
                Filter = Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? "PNG 图像 (*.png)|*.png|所有文件 (*.*)|*.*"
                    : "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*", AddExtension = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(this) == true) { e.ResultFilePath = dialog.FileName; e.Handled = true; }
            else e.Cancel = true;
        }
        finally { deferral.Complete(); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            server?.Services.GetRequiredService<ProgramRunner>().Stop();
            server?.Services.GetRequiredService<ProjectStore>().StopAllRobots();
            var dirty = server?.Services.GetRequiredService<ProjectStore>().Get().Dirty == true;
            if (pageReady)
            {
                try { dirty |= await webView.ExecuteScriptAsync("window.axisLabHasUnsavedChanges?.() ?? false") == "true"; }
                catch (Exception ex) { DesktopPaths.Log("Close state query: " + ex.Message); dirty = true; }
            }
            if (dirty && MessageBox.Show(this, "工程或属性有尚未保存的修改。\n\n确定放弃修改并退出？选择“否”返回界面继续保存。",
                "退出 AxisLab", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { closing = false; return; }
            lifetime.Cancel();
            await startup;
            webView.Dispose();
            if (server is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await server.StopAsync(timeout.Token); }
                finally { await server.DisposeAsync(); }
            }
            DesktopPaths.Log("DesktopStopped");
        }
        catch (Exception ex) { DesktopPaths.Log("Shutdown: " + ex); }
        finally
        {
            if (closing) { allowClose = true; lifetime.Dispose(); Close(); }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}


