using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace AxisLab.VisionDemo
{
    public static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);
        [STAThread]
        public static int Main(string[] args)
        {
            var root = Environment.GetEnvironmentVariable("AXISLAB_VM_ROOT") ?? @"C:\Program Files\VisionMaster4.3.0";
            var assemblies = Path.Combine(root, @"Development\V4.x\ComControls\Assembly");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                var name = new AssemblyName(e.Name).Name + ".dll";
                var path = Path.Combine(assemblies, name); return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var applications = Path.Combine(root, "Applications");
            if (Directory.Exists(applications))
            {
                SetDllDirectory(applications);
                Environment.SetEnvironmentVariable("PATH", applications + ";" + Path.Combine(applications, "3rdLib") + ";" + Environment.GetEnvironmentVariable("PATH"));
            }
            try
            {
                var app = new Application(); var window = new MainWindow();
                if (args.Length == 3 && args[0] == "--smoke") window.Loaded += async (sender, e) =>
                {
                    try { await window.Smoke(args[1], args[2]); await window.ShutdownServices(); app.Shutdown(0); }
                    catch (Exception error) { File.WriteAllText(args[2], Json.Write(new { success = false, error = error.ToString() })); await window.ShutdownServices(); app.Shutdown(1); }
                };
                return app.Run(window);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "AxisLab Vision Demo"); return 1; }
        }
    }
}
