using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageSourceModuleCs;
using VM.Core;
using VM.PlatformSDKCS;

namespace AxisLab.VisionDemo
{
    /// <summary>All VM operations share one STA. A timed-out native call never authorizes movement.</summary>
    public sealed class VisionService : IDisposable
    {
        private readonly TaskCompletionSource<Dispatcher> ready = new TaskCompletionSource<Dispatcher>();
        private readonly Thread thread;
        private VmProcedure procedure;
        private ImageSourceModuleTool source;
        private string path;
        private bool unhealthy;
        private int requestSerial = Math.Abs(Guid.NewGuid().GetHashCode() % 1000000000) + 1;
        public string LoadedHash { get; private set; }
        public bool Loaded => !unhealthy && !string.IsNullOrEmpty(LoadedHash);
        public VisionService()
        {
            thread = new Thread(() => { ready.SetResult(Dispatcher.CurrentDispatcher); Dispatcher.Run(); }) { IsBackground = true, Name = "VisionMaster STA" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        private async Task<T> Invoke<T>(Func<T> action, int timeoutMilliseconds = 20000)
        {
            if (unhealthy) throw new InvalidOperationException("VM 调用已超时，请关闭并重新打开 Demo，不能继续使用旧结果。");
            var dispatcher = await ready.Task;
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(new Action(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } }));
            if (await Task.WhenAny(completion.Task, Task.Delay(timeoutMilliseconds)) != completion.Task)
            { unhealthy = true; LoadedHash = null; throw new TimeoutException($"VM SDK 超过 {timeoutMilliseconds / 1000} 秒未返回。本次结果已丢弃，请检查本机 VM 环境、授权或流程并重启 Demo。"); }
            return await completion.Task;
        }
        public async Task Load(string solutionPath, string password, string flow, string imageSource)
        {
            LoadedHash = null;
            var sdkLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log", "SDK", "PlatformSDK.log");
            long logStart = 0;
            try { if (File.Exists(sdkLog)) logStart = new FileInfo(sdkLog).Length; } catch (IOException) { }
            try { await Invoke(() =>
            {
                path = Path.GetFullPath(solutionPath);
                var hash = Json.Hash(File.ReadAllBytes(path));
                VmSolution.Load(path, password, true);
                procedure = VmSolution.Instance[flow] as VmProcedure ?? throw new InvalidOperationException("找不到流程：" + flow);
                source = procedure[imageSource] as ImageSourceModuleTool ?? throw new InvalidOperationException("找不到图像源模块：" + imageSource);
                source.ModuParams.ImageSourceType = ImageSourceParam.ImageSourceTypeEnum.SDK;
                source.EnableResultCallback(); procedure.EnableResultCallback(); LoadedHash = hash;
                return true;
            }, 90000); }
            catch (TimeoutException error)
            {
                // Diagnose only text appended during this load; never change vendor licensing or trust settings.
                try
                {
                    using (var stream = new FileStream(sdkLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Position = Math.Max(Math.Min(logStart, stream.Length), stream.Length - 65536);
                        using (var reader = new StreamReader(stream))
                        {
                            var appended = reader.ReadToEnd();
                            if (appended.IndexOf("Dongle check fail. DongleStatus = [e0000700]", StringComparison.OrdinalIgnoreCase) >= 0)
                                throw new InvalidOperationException("VM SDK 初始化日志报告 0xE0000700：加密狗未检测到或检测异常。请检查本机 SDK 的授权识别；本次没有产生视觉目标。日志：" + sdkLog, error);
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        public static string Signature(string flow, string sourceName, bool mono, OutputMap map) =>
            Json.Hash(Encoding.UTF8.GetBytes(Json.Write(new { flow, sourceName, mono, map })));
        public string CurrentHash()
        {
            if (!Loaded) throw new InvalidOperationException("请先加载用户的 VM 方案。");
            var current = Json.Hash(File.ReadAllBytes(path));
            if (current != LoadedHash) throw new InvalidOperationException("磁盘上的 VM 方案已改变，请重新加载并验证标定。");
            return current;
        }
        public static BitmapSource Decode(byte[] png)
        {
            using (var stream = new MemoryStream(png))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var image = decoder.Frames[0]; image.Freeze(); return image;
            }
        }
        public static byte[] Pixels(byte[] png, bool mono, out int width, out int height)
        {
            var original = Decode(png); width = original.PixelWidth; height = original.PixelHeight;
            var converted = new FormatConvertedBitmap(original, mono ? PixelFormats.Gray8 : PixelFormats.Rgb24, null, 0);
            var stride = width * (mono ? 1 : 3); var bytes = new byte[stride * height]; converted.CopyPixels(bytes, stride, 0); return bytes;
        }
        private ImageBaseData Input(Frame frame, bool mono)
        {
            var bytes = Pixels(frame.Png, mono, out var width, out var height);
            if (width != frame.Info.width || height != frame.Info.height) throw new InvalidDataException("PNG 尺寸与帧信息不一致。");
            source.ModuParams.PixelFormat = mono ? ImageSourceParam.PixelFormatEnum.MONO8 : ImageSourceParam.PixelFormatEnum.RGB24;
            var image = new ImageBaseData(bytes, (uint)bytes.Length, width, height, mono ? VMPixelFormat.VM_PIXEL_MONO_08 : VMPixelFormat.VM_PIXEL_RGB24_C3);
            try { source.SetImageData(image, true); return image; } catch { image.Dispose(); throw; }
        }
        private double Float(string name)
        {
            var value = procedure.ModuResult.GetOutputFloat(name);
            if (value.nValueNum != 1 || value.pFloatVal == null) throw new InvalidOperationException("VM 输出必须是恰好一个浮点值：" + name);
            var number = value.pFloatVal[0]; if (!Json.Finite(number)) throw new InvalidOperationException("VM 输出不是有限数：" + name); return number;
        }
        private int Integer(string name)
        {
            var value = procedure.ModuResult.GetOutputInt(name);
            if (value.nValueNum != 1 || value.pIntVal == null) throw new InvalidOperationException("VM 输出必须是恰好一个整数：" + name);
            return value.pIntVal[0];
        }
        private bool Flag(string name)
        {
            var value = Integer(name);
            if (value != 0 && value != 1) throw new InvalidOperationException("VM 状态输出仅接受 0/1：" + name);
            return value == 1;
        }
        public async Task<VisionResult> Run(Frame frame, bool mono, OutputMap map, string signature)
        {
            var hash = CurrentHash();
            return await Invoke(() =>
            {
                var request = ++requestSerial;
                procedure.ModuParams.SetInputInt(map.RequestInput, new[] { request });
                var image = Input(frame, mono);
                try { procedure.Run("", true); } finally { image.Dispose(); }
                if (Integer(map.RequestOutput) != request) throw new InvalidOperationException("VM 返回的请求编号不匹配，不能使用旧流程结果。");
                var result = new VisionResult { FrameId = frame.Info.frameId, SolutionHash = hash, FlowSignature = signature,
                    RequestId = request, CalibrationOk = Flag(map.Calibration), Found = Flag(map.Found) };
                if (result.CalibrationOk && result.Found) { result.X = Float(map.X); result.Y = Float(map.Y); result.Rz = Float(map.Rz); }
                return result;
            });
        }
        // Explicit diagnostic: only runs the real image source, never produces a target or moves a robot.
        public async Task<string> VerifyInput(Frame frame, bool mono)
        {
            CurrentHash();
            return await Invoke(() =>
            {
                var image = Input(frame, mono);
                try { source.Run(); } finally { image.Dispose(); }
                var output = source.ModuResult.ImageData;
                // ModuResult owns this image; only the input buffer above belongs to this adapter.
                if (output.Width != frame.Info.width || output.Height != frame.Info.height) throw new InvalidOperationException("VM 接收的图像尺寸不一致。");
                var expected = Pixels(frame.Png, mono, out _, out _);
                if (Json.Hash(expected) != Json.Hash(output.ImageData)) throw new InvalidOperationException("VM 图像源返回的像素与本次 AxisLab 图像不一致。");
                return $"真实 SDK 送图通过：{output.Width} × {output.Height}，像素 SHA-256 相同。";
            });
        }
        public async Task Close()
        {
            if (!ready.Task.IsCompleted) return;
            var dispatcher = ready.Task.Result;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.BeginInvoke(new Action(() => { try { if (procedure != null) VmSolution.Instance.Dispose(); completion.SetResult(true); } catch (Exception e) { completion.SetException(e); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            if (await Task.WhenAny(completion.Task, Task.Delay(5000)) == completion.Task) await completion.Task;
            LoadedHash = null;
        }
        public void Dispose() { _ = Close(); }
    }
}
