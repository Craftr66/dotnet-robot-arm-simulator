using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxisLab.WpfDemo.VisionBridge;

namespace AxisLab.WpfDemo;

public sealed class VmBridgeClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _worker;
    private string? _temporary;
    private static readonly JsonSerializerOptions Wire = new() { PropertyNameCaseInsensitive = true, IncludeFields = true };
    public bool IsLoaded { get; private set; }
    public string? LoadedHash { get; private set; }
    public int? WorkerId => _worker is { HasExited: false } ? _worker.Id : null;
    private async Task Start(CancellationToken ct)
    {
        if (_worker is { HasExited: false } && _pipe?.IsConnected == true) return;
        Abort();
        var executable = Environment.GetEnvironmentVariable("AXISLAB_VM_BRIDGE") ?? Path.Combine(AppContext.BaseDirectory, "VmBridge", "AxisLab.VmBridge.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少配套 VM 服务，请保留完整发布目录。", executable);
        var name = Guid.NewGuid().ToString("D");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var data = Environment.GetEnvironmentVariable("AXISLAB_WPF_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxisLab.WpfDemo");
        _temporary = Path.Combine(data, "VmBridge", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_temporary);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _temporary };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name);
        _worker = Process.Start(start) ?? throw new InvalidOperationException("无法启动 VM 服务。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try { await _pipe.WaitForConnectionAsync(deadline.Token); }
        catch { Abort(); throw; }
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }
    private async Task<T> Request<T>(BridgeRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await Start(ct);
            request.Id = Guid.NewGuid().ToString("D");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(request.Command == "load" ? 100 : 30));
            using var abort = deadline.Token.Register(Abort);
            var writer = _writer!; var reader = _reader!;
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Wire).AsMemory(), deadline.Token);
            var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("VM 服务已退出，请重新加载方案。");
            var reply = JsonSerializer.Deserialize<BridgeReply>(line, Wire) ?? throw new IOException("VM 服务返回空响应。");
            if (reply.Id != request.Id) throw new InvalidDataException("VM 服务响应身份不匹配。");
            if (!reply.Success) throw new InvalidOperationException(reply.Error ?? "VM 执行失败。");
            return JsonSerializer.Deserialize<T>(reply.Payload, Wire) ?? throw new InvalidDataException("VM 结果为空。");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        { Abort(); throw new InvalidOperationException("VM 通信已中断，旧结果已失效；请重新加载方案。", e); }
        finally { _gate.Release(); }
    }
    public async Task Load(string path, string password, string flow, string source, CancellationToken ct)
    {
        IsLoaded = false; LoadedHash = null;
        var reply = await Request<LoadReply>(new BridgeRequest { Command = "load", SolutionPath = Path.GetFullPath(path), Password = password, Flow = flow, Source = source }, ct);
        LoadedHash = reply.Hash; IsLoaded = true;
    }
    private BridgeRequest ImageRequest(string command, CapturedFrame frame, VmSettings settings)
    {
        if (!IsLoaded || _temporary is null) throw new InvalidOperationException("请先加载 VM 方案。");
        var input = Path.Combine(_temporary, frame.FrameId.ToString("D") + ".png"); File.WriteAllBytes(input, frame.Png);
        return new BridgeRequest { Command = command, InputFile = input, InputSha = frame.Sha256, FrameId = frame.FrameId.ToString("D"), Width = frame.Width, Height = frame.Height,
            Flow = settings.Flow, Source = settings.Source, Mono = settings.Mono, MappingJson = JsonSerializer.Serialize(settings.Map, Wire) };
    }
    public async Task<string> Verify(CapturedFrame frame, VmSettings settings, CancellationToken ct) => (await Request<VerifyReply>(ImageRequest("verify", frame, settings), ct)).Message;
    public Task<VmResult> Run(CapturedFrame frame, VmSettings settings, CancellationToken ct) => Request<VmResult>(ImageRequest("run", frame, settings), ct);
    private sealed class LoadReply { public string Hash { get; set; } = ""; }
    private sealed class VerifyReply { public string Message { get; set; } = ""; }
    public void Abort()
    {
        IsLoaded = false; LoadedHash = null;
        try { _pipe?.Dispose(); } catch { }
        try { if (_worker is { HasExited: false }) _worker.Kill(entireProcessTree: true); } catch { }
        _pipe = null;
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe?.IsConnected == true && _worker is { HasExited: false })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await Request<JsonElement>(new BridgeRequest { Command = "close" }, timeout.Token);
                await _worker.WaitForExitAsync(timeout.Token);
            }
        }
        catch { }
        finally
        {
            Abort(); _worker?.Dispose(); _worker = null;
            // Only remove our unique, recorded temporary directory, never a user-supplied path.
            if (_temporary is not null) try { Directory.Delete(_temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _temporary = null;
        }
    }
}
