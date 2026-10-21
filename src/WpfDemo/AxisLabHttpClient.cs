using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AxisLab.WpfDemo;

public sealed class AxisLabHttpException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? Code { get; }

    public AxisLabHttpException(HttpStatusCode statusCode, string? code, string message)
        : base($"HTTP {(int)statusCode}: {message}")
    {
        StatusCode = statusCode;
        Code = code;
    }
}

public sealed class AxisLabHttpClient : IDisposable
{
    private readonly HttpClient _http;

    public Uri BaseUri { get; }

    public AxisLabHttpClient(Uri baseUri)
    {
        BaseUri = baseUri;
        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public Task<HealthInfo> GetHealthAsync(CancellationToken ct = default) =>
        GetJsonAsync<HealthInfo>("api/health", ct);

    public Task<CommunicationStatus> GetCommunicationAsync(CancellationToken ct = default) =>
        GetJsonAsync<CommunicationStatus>("api/v1/communication", ct);

    public Task<ProjectSnapshot> GetProjectAsync(CancellationToken ct = default) =>
        GetJsonAsync<ProjectSnapshot>("api/v1/project", ct);

    public Task<ProjectListEntry[]> GetProjectsAsync(CancellationToken ct = default) =>
        GetJsonAsync<ProjectListEntry[]>("api/v1/projects", ct);

    public Task<DeviceDocument[]> GetDevicesAsync(CancellationToken ct = default) =>
        GetJsonAsync<DeviceDocument[]>("api/v1/devices", ct);

    public Task<RobotModelInfo[]> GetRobotModelsAsync(CancellationToken ct = default) =>
        GetJsonAsync<RobotModelInfo[]>("api/v1/robot-models", ct);

    public Task<RuntimeSnapshot> GetRuntimeAsync(CancellationToken ct = default) => GetJsonAsync<RuntimeSnapshot>("api/v1/runtime", ct);

    public Task<JsonElement> PreviewCartesianAsync(Guid robot, TransformInfo target, double speed, bool positionOnly, CancellationToken ct = default) =>
        PostJsonAsync<JsonElement>($"api/v1/robots/{robot}/preview", new { target, frame = "base", kind = "MoveJ", speedPercent = speed, positionOnly }, ct);

    public Task<ProjectSnapshot> OpenProjectAsync(Guid fileId, int expectedRevision, CancellationToken ct = default) =>
        PostJsonAsync<ProjectSnapshot>("api/v1/project/open",
            new { fileId, expectedRevision }, ct);

    public Task<ProjectSnapshot> ImportProjectAsync(JsonElement project, int expectedRevision, CancellationToken ct = default) =>
        PostJsonAsync<ProjectSnapshot>("api/v1/project/import",
            new { project, expectedRevision }, ct);

    public Task<ProjectSnapshot> NewProjectAsync(int expectedRevision, string name, CancellationToken ct = default) =>
        PostJsonAsync<ProjectSnapshot>("api/v1/project/new",
            new { expectedRevision, name }, ct);

    /// <summary>本地 UI 的点动接口；仅用于验证外部接管时本地控制被锁定，外部客户端不应依赖。</summary>
    public Task<JsonElement> MoveLocalAsync(Guid robotId, double[] joints, double speedPercent, int expectedRevision, CancellationToken ct = default) =>
        PostJsonAsync<JsonElement>($"api/v1/robots/{robotId}/joints",
            new { joints, speedPercent, expectedRevision }, ct);

    /// <summary>本地 UI 的停止接口；仅用于边界验证。</summary>
    public Task<JsonElement> StopLocalAsync(Guid robotId, CancellationToken ct = default) =>
        PostJsonAsync<JsonElement>($"api/v1/robots/{robotId}/stop", new { }, ct);

    public Task<JsonElement> GetFrameMetadataAsync(string sessionToken, uint frameToken, CancellationToken ct = default) =>
        GetJsonAsync<JsonElement>(
            $"api/v1/communication/frames/{Uri.EscapeDataString(sessionToken)}/{frameToken}", ct);

    public Task<JsonElement> GetCommandAsync(uint clientId, uint sequence, CancellationToken ct = default) =>
        GetJsonAsync<JsonElement>(
            $"api/v1/communication/clients/{clientId}/commands/{sequence}", ct);

    public async Task<byte[]> DownloadAsync(string url, CancellationToken ct = default)
    {
        var uri = ResolveUrl(url);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public Uri ResolveUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var absolute)
        ? absolute
        : new Uri(BaseUri, url);

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, ct).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Web, ct).ConfigureAwait(false);
        return value ?? throw new InvalidDataException("AxisLab 返回了空 JSON。");
    }

    private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, JsonDefaults.Web, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, ct).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Web, ct).ConfigureAwait(false);
        return value ?? throw new InvalidDataException("AxisLab 返回了空 JSON。");
    }

    private static async Task<AxisLabHttpException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? code = null;
        var message = string.IsNullOrWhiteSpace(text) ? response.ReasonPhrase ?? "请求失败" : text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var c)) code = c.ToString();
                if (doc.RootElement.TryGetProperty("message", out var m)) message = m.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return new AxisLabHttpException(response.StatusCode, code, message);
    }

    public void Dispose() => _http.Dispose();
}



