using System.IO;
using System.Text.RegularExpressions;

namespace AxisLab.WpfDemo;

public static class AxisLabLocator
{
    private static readonly Regex ServiceReadyPattern = new(
        @"ServiceReady\s+(http://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? FindLastServiceUrl()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AxisLab", "desktop.log");
            if (!File.Exists(path)) return null;
            string? last = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Contains("ServiceReady", StringComparison.OrdinalIgnoreCase))
                    last = line;
            }
            if (last is null) return null;
            var match = ServiceReadyPattern.Match(last);
            return match.Success ? match.Groups[1].Value.TrimEnd('/') : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsAxisLabRunning() =>
        System.Diagnostics.Process.GetProcessesByName("AxisLab").Length > 0;
}
