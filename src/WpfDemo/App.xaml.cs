using System.Windows;

namespace AxisLab.WpfDemo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.Length == 3 && e.Args[0] is "--self-test" or "--sampling-test")
        {
            window.Loaded += async (_, _) =>
            {
                try { if (e.Args[0] == "--sampling-test") await window.RunSamplingChecks(e.Args[1], e.Args[2]); else await window.RunFrameworkChecks(e.Args[1], e.Args[2]); }
                catch (Exception error) { System.IO.File.WriteAllText(e.Args[2], System.Text.Json.JsonSerializer.Serialize(new { success = false, error = error.ToString() })); Environment.ExitCode = 1; }
                window.Close();
            };
        }
        window.Show();
    }
}
