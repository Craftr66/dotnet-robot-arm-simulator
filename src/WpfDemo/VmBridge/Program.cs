using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using AxisLab.VisionDemo;
using AxisLab.WpfDemo.VisionBridge;

namespace AxisLab.VmBridge
{
    internal static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "--pipe" || !Guid.TryParse(args[1], out _)) return 2;
            var root = Environment.GetEnvironmentVariable("AXISLAB_VM_ROOT") ?? @"C:\Program Files\VisionMaster4.3.0";
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => { var file = Path.Combine(root, @"Development\V4.x\ComControls\Assembly", new AssemblyName(e.Name).Name + ".dll"); return File.Exists(file) ? Assembly.LoadFrom(file) : null; };
            var native = Path.Combine(root, "Applications"); SetDllDirectory(native);
            Environment.SetEnvironmentVariable("PATH", native + ";" + Path.Combine(native, "3rdLib") + ";" + Environment.GetEnvironmentVariable("PATH"));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (sender, e) => { var code = 0; try { await Serve(args[1]); } catch { code = 1; } app.Shutdown(code); };
            return app.Run();
        }
        private static async Task Serve(string pipeName)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(10000);
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    var vision = new VisionService();
                    var closed = false;
                    try
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            BridgeRequest request = null; object result = null; string error = null;
                            try
                            {
                                if (line.Length > 65536) throw new InvalidDataException("IPC request is too large.");
                                request = Json.Read<BridgeRequest>(line);
                                if (!Guid.TryParse(request.Id, out _)) throw new InvalidDataException("Invalid request identity.");
                                if (request.Command == "close") { await vision.Close(); closed = true; await writer.WriteLineAsync(Json.Write(new BridgeReply { Id = request.Id, Success = true, Payload = "{}" })); return; }
                                if (request.Command == "load")
                                {
                                    await vision.Load(request.SolutionPath, request.Password ?? "", request.Flow, request.Source);
                                    result = new { Hash = vision.CurrentHash() };
                                }
                                else if (request.Command == "verify" || request.Command == "run")
                                {
                                    var png = File.ReadAllBytes(request.InputFile);
                                    if (Json.Hash(png) != request.InputSha) throw new InvalidDataException("Input PNG changed before VM acquisition.");
                                    var frame = new Frame { Png = png, Info = new FrameInfo { frameId = request.FrameId, width = request.Width, height = request.Height } };
                                    if (request.Command == "verify") result = new { Message = await vision.VerifyInput(frame, request.Mono) };
                                    else
                                    {
                                        var map = Json.Read<OutputMap>(request.MappingJson);
                                        result = await vision.Run(frame, request.Mono, map, VisionService.Signature(request.Flow, request.Source, request.Mono, map));
                                    }
                                }
                                else throw new InvalidOperationException("Unknown VM bridge command.");
                            }
                            catch (Exception e) { error = e.Message; }
                            await writer.WriteLineAsync(Json.Write(new BridgeReply { Id = request?.Id, Success = error == null, Payload = result == null ? null : Json.Write(result), Error = error }));
                        }
                    }
                    finally { if (!closed) await vision.Close(); }
                }
            }
        }
    }
}
