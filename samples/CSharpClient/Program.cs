using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

// Usage: dotnet run --project samples/CSharpClient -- BASE_URL [CAMERA_ID|first] [OUTPUT_DIRECTORY]
if (args.Length == 0)
{
    Console.Error.WriteLine("用法：AxisLab.CaptureClient <软件连接地址> [相机ID或first] [保存目录]\n在 AxisLab 的“工具 → 连接信息”菜单中查看地址，并保持软件窗口打开。");
    return 1;
}
try
{
    var address = new Uri(args[0].TrimEnd('/') + '/');
    if (address.Scheme != "http" || !address.IsLoopback) throw new ArgumentException("请使用 AxisLab 的本机 HTTP 地址。");
    using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(15) };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var ct = cancellation.Token;
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    async Task<T> Json<T>(HttpResponseMessage response)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
            return (await response.Content.ReadFromJsonAsync<T>(jsonOptions, ct))!;
        }
    }
    var devices = await Json<Device[]>(await client.GetAsync("api/v1/devices", ct));
    var cameras = devices.Where(d => d.TypeId == "camera.pinhole").ToArray();
    foreach (var camera in cameras) Console.WriteLine($"相机 {camera.Id}: {camera.Name}");
    if (cameras.Length == 0) throw new InvalidOperationException("请先在软件中添加相机。");
    var id = args.Length > 1 && args[1] != "first" ? Guid.Parse(args[1]) : cameras[0].Id;
    if (!cameras.Any(d => d.Id == id)) throw new ArgumentException("相机 ID 不存在。");
    var state = await Json<Capture>(await client.PostAsync($"api/v1/cameras/{id}/captures", null, ct));
    Console.WriteLine($"触发 {state.CaptureId}，等待成像…");
    while (state.Status is "Queued" or "Rendering")
    {
        await Task.Delay(150, ct);
        state = await Json<Capture>(await client.GetAsync($"api/v1/captures/{state.CaptureId}", ct));
    }
    if (state.Status != "Completed" || state.FrameId is null) throw new InvalidOperationException($"{state.ErrorCode}: {state.Message}");
    var metadata = await Json<JsonElement>(await client.GetAsync($"api/v1/cameras/{id}/frames/{state.FrameId}", ct));
    if (metadata.GetProperty("captureId").GetGuid() != state.CaptureId || metadata.GetProperty("cameraId").GetGuid() != id)
        throw new InvalidOperationException("图像元数据与触发任务不一致。");
    var imageUrl = metadata.GetProperty("imageUrl").GetString()!;
    var png = await client.GetByteArrayAsync(imageUrl, ct);
    var sha = Convert.ToHexString(SHA256.HashData(png));
    if (!sha.Equals(metadata.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("PNG 校验失败。");
    var output = Path.GetFullPath(args.Length > 2 ? args[2] : "captures"); Directory.CreateDirectory(output);
    var path = Path.Combine(output, state.FrameId + ".png");
    await File.WriteAllBytesAsync(path, png, ct);
    await File.WriteAllTextAsync(Path.ChangeExtension(path, ".frame.json"), JsonSerializer.Serialize(metadata, jsonOptions), ct);
    Console.WriteLine($"已保存 {metadata.GetProperty("width")} × {metadata.GetProperty("height")} PNG: {path}\nSHA256 {sha}");
    return 0;
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or IOException)
{
    Console.Error.WriteLine(ex.Message); return 1;
}

record Device(Guid Id, string TypeId, string Name);
record Capture(Guid CaptureId, Guid CameraId, string Status, Guid? FrameId, string? ErrorCode, string? Message);

