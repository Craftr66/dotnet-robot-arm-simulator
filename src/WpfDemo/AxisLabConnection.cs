using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AxisLab.WpfDemo;

public sealed class AxisLabConnection : IAsyncDisposable
{
    private AxisLabHttpClient? _http;
    private ModbusTcpClient? _discovery;
    private ExternalControlSession? _robot;
    private ExternalControlSession? _stop;
    private ExternalControlSession? _camera;

    public Uri? BaseUri { get; private set; }
    public HealthInfo? Health { get; private set; }
    public CommunicationStatus? Communication { get; private set; }
    public ProjectSnapshot? Project { get; private set; }
    public RobotModelInfo[] RobotModels { get; private set; } = [];
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];
    public string? ModbusError { get; private set; }
    public bool IsHttpConnected => _http is not null && Health is not null;
    public bool IsModbusConnected => _discovery?.IsConnected == true;
    public ExternalControlSession? RobotSession => _robot;
    public ExternalControlSession? CameraSession => _camera;
    public AxisLabHttpClient Http => _http ?? throw new InvalidOperationException("尚未连接 AxisLab。");

    public async Task ConnectAsync(Uri baseUri, CancellationToken ct = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        BaseUri = baseUri;
        _http = new AxisLabHttpClient(baseUri);
        Health = await _http.GetHealthAsync(ct).ConfigureAwait(false);
        Communication = await _http.GetCommunicationAsync(ct).ConfigureAwait(false);
        Project = await _http.GetProjectAsync(ct).ConfigureAwait(false);
        RobotModels = await _http.GetRobotModelsAsync(ct).ConfigureAwait(false);

        if (Communication.Listening)
        {
            try
            {
                _discovery = new ModbusTcpClient();
                await _discovery.ConnectAsync(baseUri.Host, Communication.ModbusPort, ct).ConfigureAwait(false);
                ModbusError = null;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                ModbusError = $"Modbus 连接失败：{ex.Message}";
                _discovery = null;
            }
        }
        else
        {
            ModbusError = Communication.Error ?? "Modbus 服务未监听。";
        }

        Devices = await DiscoverDevicesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceInfo>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        var http = Http;
        var documents = await http.GetDevicesAsync(ct).ConfigureAwait(false);
        var rows = new Dictionary<ushort, (ushort Number, ushort Type, ushort Axes, ushort Capabilities)>();

        if (_discovery is { IsConnected: true })
        {
            try
            {
                var header = await _discovery.ReadHoldingRegistersAsync(0, 32, ct).ConfigureAwait(false);
                if (header[0] == 0x4158)
                {
                    var count = header[10];
                    for (var page = 0; page * 16 < count; page++)
                    {
                        await _discovery.WriteSingleRegisterAsync(201, (ushort)page, ct).ConfigureAwait(false);
                        var directory = await _discovery.ReadHoldingRegistersAsync(128, 64, ct).ConfigureAwait(false);
                        for (var i = 0; i < 16; i++)
                        {
                            var number = directory[i * 4];
                            if (number == 0) continue;
                            rows[number] = (number, directory[i * 4 + 1], directory[i * 4 + 2], directory[i * 4 + 3]);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or InvalidDataException or OperationCanceledException)
            {
                ModbusError = $"Modbus 设备目录读取失败：{ex.Message}";
            }
        }

        var result = new List<DeviceInfo>();
        foreach (var doc in documents)
        {
            rows.TryGetValue(doc.DeviceNumber, out var row);
            var type = row.Type != 0 ? row.Type : AxisLabTypes.InferType(doc.TypeId);
            var caps = row.Capabilities != 0 ? row.Capabilities : AxisLabTypes.InferCapabilities(doc.TypeId);
            var axes = row.Axes != 0 ? row.Axes : (ushort)(doc.Robot?.JointPositions.Length ?? 0);
            result.Add(new DeviceInfo
            {
                Number = doc.DeviceNumber,
                Name = doc.Name,
                TypeId = doc.TypeId,
                Type = type,
                Axes = axes,
                Capabilities = caps,
                Id = doc.Id,
                ParentId = doc.ParentId,
                Transform = doc.Transform,
                Parameters = doc.Parameters,
                Robot = doc.Robot,
                ConfigurationRevision = doc.ConfigurationRevision
            });
        }

        foreach (var row in rows.Values.Where(r => documents.All(d => d.DeviceNumber != r.Number)))
        {
            result.Add(new DeviceInfo
            {
                Number = row.Number,
                Name = $"设备 #{row.Number}",
                TypeId = "",
                Type = row.Type,
                Axes = row.Axes,
                Capabilities = row.Capabilities
            });
        }

        Devices = result.OrderBy(d => d.Number).ToArray();
        return Devices;
    }

    public async Task<ProjectSnapshot> RefreshProjectAsync(CancellationToken ct = default)
    {
        var previousSession = Project?.SessionId;
        Project = await Http.GetProjectAsync(ct).ConfigureAwait(false);
        RobotModels = await Http.GetRobotModelsAsync(ct).ConfigureAwait(false);
        if (previousSession is not null && previousSession != Project.SessionId)
            await ResetDeviceSessionsAsync().ConfigureAwait(false);
        Devices = await DiscoverDevicesAsync(ct).ConfigureAwait(false);
        return Project;
    }

    public async Task<ProjectSnapshot> OpenProjectAsync(Guid fileId, CancellationToken ct = default)
    {
        var current = Project ?? throw new InvalidOperationException("尚未连接 AxisLab。");
        await ResetDeviceSessionsAsync().ConfigureAwait(false);
        Project = await Http.OpenProjectAsync(fileId, current.Revision, ct).ConfigureAwait(false);
        RobotModels = await Http.GetRobotModelsAsync(ct).ConfigureAwait(false);
        Devices = await DiscoverDevicesAsync(ct).ConfigureAwait(false);
        return Project;
    }

    public async Task<ProjectSnapshot> ImportProjectAsync(JsonElement project, CancellationToken ct = default)
    {
        var current = Project ?? throw new InvalidOperationException("尚未连接 AxisLab。");
        await ResetDeviceSessionsAsync().ConfigureAwait(false);
        Project = await Http.ImportProjectAsync(project, current.Revision, ct).ConfigureAwait(false);
        RobotModels = await Http.GetRobotModelsAsync(ct).ConfigureAwait(false);
        Devices = await DiscoverDevicesAsync(ct).ConfigureAwait(false);
        return Project;
    }

    public async Task<DeviceState> SelectRobotAsync(DeviceInfo device, CancellationToken ct = default)
    {
        if (!device.IsRobot) throw new ArgumentException("所选设备不是机器人。", nameof(device));
        if (Communication is null) throw new InvalidOperationException("尚未连接 AxisLab。");
        if (_robot is not null) await _robot.DisposeAsync().ConfigureAwait(false);
        _robot = new ExternalControlSession(Http, BaseUri!.Host, Communication.ModbusPort);
        await _robot.ConnectAsync(ct).ConfigureAwait(false);
        var state = await _robot.SelectDeviceAsync(device.Number, ct).ConfigureAwait(false);
        if (state.SelectedType is not (AxisLabTypes.Robot3R or AxisLabTypes.Robot6R))
            throw new InvalidOperationException($"设备 #{device.Number} 的 Modbus 类型为 {state.SelectedType}，不是机器人。");

        // 独立 Stop 连接：运动期间也能立即停止，不会被运动命令的轮询阻塞。
        if (_stop is not null) await _stop.DisposeAsync().ConfigureAwait(false);
        _stop = new ExternalControlSession(Http, BaseUri!.Host, Communication.ModbusPort);
        try
        {
            await _stop.ConnectAsync(ct).ConfigureAwait(false);
            await _stop.SelectDeviceAsync(device.Number, ct).ConfigureAwait(false);
        }
        catch
        {
            await _stop.DisposeAsync().ConfigureAwait(false);
            _stop = null;
        }
        return state;
    }

    public async Task<DeviceState> SelectCameraAsync(DeviceInfo device, CancellationToken ct = default)
    {
        if (!device.IsCamera) throw new ArgumentException("所选设备不是相机。", nameof(device));
        if (Communication is null) throw new InvalidOperationException("尚未连接 AxisLab。");
        if (_camera is not null) await _camera.DisposeAsync().ConfigureAwait(false);
        _camera = new ExternalControlSession(Http, BaseUri!.Host, Communication.ModbusPort);
        await _camera.ConnectAsync(ct).ConfigureAwait(false);
        var state = await _camera.SelectDeviceAsync(device.Number, ct).ConfigureAwait(false);
        if (state.SelectedType != AxisLabTypes.Camera)
            throw new InvalidOperationException($"设备 #{device.Number} 的 Modbus 类型为 {state.SelectedType}，不是相机。");
        return state;
    }

    public async Task<DeviceState> AcquireRobotControlAsync(CancellationToken ct = default) =>
        await RequireRobotSession().AcquireControlAsync(ct).ConfigureAwait(false);

    public async Task<DeviceState> ReleaseRobotControlAsync(CancellationToken ct = default) =>
        await RequireRobotSession().ReleaseControlAsync(ct).ConfigureAwait(false);

    public async Task<DeviceState> StopRobotAsync(CancellationToken ct = default)
    {
        var state = await (_stop ?? RequireRobotSession()).StopAsync(ct).ConfigureAwait(false);
        if (_robot is not null) await _robot.NotifyControlRevokedAsync().ConfigureAwait(false);
        return state;
    }

    public async Task<DeviceState> MoveRobotAsync(double[] joints, double speedPercent, CancellationToken ct = default) =>
        await RequireRobotSession().MoveJAsync(joints, speedPercent, ct).ConfigureAwait(false);

    public Task<DeviceState> ReadRobotStateAsync(CancellationToken ct = default) =>
        RequireRobotSession().ReadStateAsync(ct);

    public async Task<CapturedFrame> CaptureAsync(DeviceInfo camera, CancellationToken ct = default)
    {
        if (_camera is null || _camera.SelectedNumber != camera.Number || !_camera.IsSelected)
            await SelectCameraAsync(camera, ct).ConfigureAwait(false);

        var state = await _camera!.CaptureAsync(ct).ConfigureAwait(false);
        var token = state.SessionTokenHex;
        var metadata = await Http.GetFrameMetadataAsync(token, state.FrameToken, ct).ConfigureAwait(false);
        if (metadata.GetProperty("cameraId").GetGuid() != camera.Id || metadata.GetProperty("cameraConfigurationRevision").GetUInt32() != camera.ConfigurationRevision)
            throw new InvalidDataException("拍摄相机或配置已变化，请刷新设备后重拍。");
        var imageUrl = metadata.TryGetProperty("imageUrl", out var url)
            ? url.GetString()
            : throw new InvalidDataException("帧元数据缺少 imageUrl。");
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidDataException("帧元数据 imageUrl 为空。");

        var png = await Http.DownloadAsync(imageUrl, ct).ConfigureAwait(false);
        var metadataSha = metadata.TryGetProperty("sha256", out var sha)
            ? sha.GetString()
            : throw new InvalidDataException("帧元数据缺少 sha256。");
        if (string.IsNullOrWhiteSpace(metadataSha))
            throw new InvalidDataException("帧元数据 sha256 为空。");
        var actualSha = Convert.ToHexString(SHA256.HashData(png));
        if (!actualSha.Equals(metadataSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"PNG SHA-256 校验失败：期望 {metadataSha}，实际 {actualSha}。");

        return new CapturedFrame
        {
            Png = png,
            Metadata = metadata,
            MetadataJson = metadata.GetRawText(),
            FrameId = metadata.GetProperty("frameId").GetGuid(),
            CaptureId = metadata.GetProperty("captureId").GetGuid(),
            CameraId = metadata.GetProperty("cameraId").GetGuid(),
            Width = metadata.GetProperty("width").GetInt32(),
            Height = metadata.GetProperty("height").GetInt32(),
            Sha256 = metadataSha,
            SessionToken = token,
            FrameToken = state.FrameToken
        };
    }

    public async Task ResetDeviceSessionsAsync()
    {
        if (_robot is not null)
        {
            await _robot.DisposeAsync().ConfigureAwait(false);
            _robot = null;
        }
        if (_stop is not null)
        {
            await _stop.DisposeAsync().ConfigureAwait(false);
            _stop = null;
        }
        if (_camera is not null)
        {
            await _camera.DisposeAsync().ConfigureAwait(false);
            _camera = null;
        }
    }

    private ExternalControlSession RequireRobotSession() =>
        _robot ?? throw new InvalidOperationException("请先在设备列表中选择一个机器人。");

    public async Task DisconnectAsync()
    {
        await ResetDeviceSessionsAsync().ConfigureAwait(false);
        if (_discovery is not null)
        {
            await _discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
        }
        _http?.Dispose();
        _http = null;
        BaseUri = null;
        Health = null;
        Communication = null;
        Project = null;
        RobotModels = [];
        Devices = [];
        ModbusError = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}


