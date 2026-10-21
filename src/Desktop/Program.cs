using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AxisLab.Desktop;

internal static class Program
{
    private static string ProfileScope => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AXISLAB_DESKTOP_HOME")) ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DesktopPaths.Home.ToUpperInvariant())))[..12];
    public static string WindowTitle => "AxisLab · 机械臂与视觉仿真" + (ProfileScope.Length > 0 ? " · " + ProfileScope : "");

    [STAThread]
    private static void Main()
    {
        var userId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var singleInstance = new Mutex(true, @"Local\AxisLab.Desktop." + userId + ProfileScope, out var acquired);
        if (!acquired)
        {
            var existing = FindWindow(null, WindowTitle);
            if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
            else MessageBox.Show("AxisLab 正在启动，请稍候。", "AxisLab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, args) =>
            {
                DesktopPaths.Log(args.Exception.ToString());
                MessageBox.Show("操作遇到错误，请重启程序。工程文件不会被自动覆盖。\n\n" + args.Exception.Message,
                    "AxisLab", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            app.Run(new MainWindow());
        }
        catch (Exception ex)
        {
            DesktopPaths.Log(ex.ToString());
            MessageBox.Show("AxisLab 无法启动。\n\n" + ex.Message, "AxisLab", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { singleInstance.ReleaseMutex(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

internal static class DesktopPaths
{
    public static readonly string Home = Path.GetFullPath(Environment.GetEnvironmentVariable("AXISLAB_DESKTOP_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxisLab"));
    public static string Projects => Path.Combine(Home, "Projects");
    public static string WebViewData => Path.Combine(Home, "WebView2");
    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(Home);
            File.AppendAllText(Path.Combine(Home, "desktop.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
