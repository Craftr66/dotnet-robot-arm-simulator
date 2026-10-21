using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AxisLab.WpfDemo;

/// <summary>
/// 一条 Modbus TCP 连接上的“选择 + 暂存 + 提交 + 心跳”业务会话。
/// 一个连接同一时间只能选择一个设备；机器人和相机各用独立连接。
/// </summary>
public sealed class ExternalControlSession : IAsyncDisposable
{
    private readonly AxisLabHttpClient _http;
    private readonly ModbusTcpClient _modbus = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private uint _sequence;
    private uint _heartbeatCounter;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;

    public string Host { get; }
    public int Port { get; }
    public DeviceState? LastState { get; private set; }
    public bool IsHoldingControl { get; private set; }
    public bool IsSelected => LastState?.IdentityValid == true;
    public ushort SelectedNumber => LastState?.SelectedDeviceNumber ?? 0;
    public ushort SelectedType => LastState?.SelectedType ?? 0;

    public event EventHandler<Exception>? HeartbeatFailed;

    public ExternalControlSession(AxisLabHttpClient http, string host, int port)
    {
        _http = http;
        Host = host;
        Port = port;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _modbus.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
    }

    public async Task<DeviceState> SelectDeviceAsync(ushort deviceNumber, CancellationToken ct = default)
    {
        if (IsHoldingControl)
            throw new InvalidOperationException("当前连接仍持有机器人控制权，不能切换设备。请先释放控制权。");
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _modbus.WriteSingleRegisterAsync(200, deviceNumber, ct).ConfigureAwait(false);
            var state = await ReadStateAsync(ct).ConfigureAwait(false);
            if (state.Magic != 0x4158)
                throw new InvalidDataException("不是受支持的 AxisLab Modbus v1 服务。");
            if (state.SelectedDeviceNumber != deviceNumber || !state.IdentityValid)
                throw new InvalidOperationException($"设备 #{deviceNumber} 选择失败：身份在本次工程会话中无效。");
            LastState = state;
            return state;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<DeviceState> ReadStateAsync(CancellationToken ct = default)
    {
        var registers = await _modbus.ReadHoldingRegistersAsync(0, 120, ct).ConfigureAwait(false);
        var state = ModbusCodec.ParseState(registers);
        LastState = state;
        return state;
    }

    public async Task<DeviceState> AcquireControlAsync(CancellationToken ct = default)
    {
        if (IsHoldingControl)
            return await ReadStateAsync(ct).ConfigureAwait(false);
        var state = await ExecuteCommandAsync(1, 0, null, 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!state.IsOwnedByThisConnection)
            throw new ModbusCommandException(state.CommandState, state.ErrorCode,
                $"申请控制权未生效：控制权状态={state.ControlOwner}。");
        IsHoldingControl = true;
        await StartHeartbeatAsync().ConfigureAwait(false);
        return state;
    }

    public async Task<DeviceState> ReleaseControlAsync(CancellationToken ct = default)
    {
        if (!IsHoldingControl)
        {
            await StopHeartbeatAsync().ConfigureAwait(false);
            return await ReadStateAsync(ct).ConfigureAwait(false);
        }
        await StopHeartbeatAsync().ConfigureAwait(false);
        try
        {
            var state = await ExecuteCommandAsync(2, 0, null, 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return state;
        }
        finally
        {
            IsHoldingControl = false;
        }
    }

    /// <summary>服务端已撤销控制权（本地停止、其他连接 Stop 或心跳超时）时通知本地会话。</summary>
    public async Task NotifyControlRevokedAsync()
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        IsHoldingControl = false;
    }

    /// <summary>停止运动；不要求持有控制权，服务端会同时撤销外部控制。</summary>
    public async Task<DeviceState> StopAsync(CancellationToken ct = default)
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        try
        {
            var state = await ExecuteCommandAsync(4, 0, null, 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return state;
        }
        finally
        {
            IsHoldingControl = false;
        }
    }

    public async Task<DeviceState> MoveJAsync(double[] joints, double speedPercent, CancellationToken ct = default)
    {
        if (!IsHoldingControl)
            throw new InvalidOperationException("尚未持有机器人控制权。请先点击“接管控制”。");
        var state = LastState ?? await ReadStateAsync(ct).ConfigureAwait(false);
        var axes = state.Axes;
        if (axes is not (3 or 6))
            throw new InvalidOperationException($"所选设备不是可运动机器人（轴数={axes}）。");
        if (joints.Length != axes)
            throw new ArgumentException($"该设备需要 {axes} 个关节目标，当前提供 {joints.Length} 个。", nameof(joints));
        if (speedPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(speedPercent), "速度必须在 1—100%。");
        return await ExecuteCommandAsync(3, axes, joints, speedPercent, TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
    }

    public async Task<DeviceState> CaptureAsync(CancellationToken ct = default)
    {
        var state = LastState ?? await ReadStateAsync(ct).ConfigureAwait(false);
        if (state.SelectedType != AxisLabTypes.Camera)
            throw new InvalidOperationException("所选设备不是相机。");
        var result = await ExecuteCommandAsync(5, 0, null, 0, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        if (result.FrameToken == 0)
            throw new ModbusCommandException(result.CommandState, result.ErrorCode, "拍照完成但未返回 frameToken。");
        return result;
    }

    public async Task<DeviceState> MoveCartesianAsync(TransformInfo target, double speed, CancellationToken ct = default)
    {
        if (!IsHoldingControl) throw new InvalidOperationException("请先接管机器人控制权。");
        var state = await ReadStateAsync(ct).ConfigureAwait(false);
        var values = new[] { target.X, target.Y, target.Z, target.Rx, target.Ry, target.Rz };
        if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > float.MaxValue) || !double.IsFinite(speed) || speed is < 1 or > 100)
            throw new ArgumentException("目标或速度无效。");
        return await ExecuteCommandAsync(7, state.Axes, values, speed, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
    }

    private async Task<DeviceState> ExecuteCommandAsync(
        ushort opcode, ushort axes, double[]? joints, double speed,
        TimeSpan timeout, CancellationToken ct)
    {
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(ct).ConfigureAwait(false);
            if (state.Magic != 0x4158)
                throw new InvalidDataException("不是受支持的 AxisLab Modbus v1 服务。");
            if (!state.IdentityValid)
                throw new ModbusCommandException(state.CommandState, state.ErrorCode,
                    "当前选择已失效（工程新建/导入/重开或服务重启），请重新选择设备。");

            var bank = new ushort[28];
            bank[0] = opcode;
            bank[1] = opcode == 7 && axes == 3 ? (ushort)1 : (ushort)0;
            bank[2] = axes;
            bank[3] = 0;
            if (joints is not null)
            {
                if (joints.Length > 6) throw new ArgumentException("最多 6 个关节目标。", nameof(joints));
                for (var i = 0; i < joints.Length; i++)
                    ModbusCodec.PutFloat(bank, 4 + i * 2, (float)joints[i]);
            }
            ModbusCodec.PutFloat(bank, 16, (float)speed);
            for (var i = 0; i < 4; i++) bank[18 + i] = (ushort)((state.SessionToken >> (48 - i * 16)) & 0xFFFF);
            bank[22] = (ushort)(state.ConfigurationRevision >> 16);
            bank[23] = (ushort)state.ConfigurationRevision;
            // 226—229 必须为零，数组默认值已满足。

            await _modbus.WriteMultipleRegistersAsync(202, bank, ct).ConfigureAwait(false);
            var sequence = NextSequence();
            await _modbus.WriteMultipleRegistersAsync(232, ModbusCodec.UInt32Words(sequence), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(120, ct).ConfigureAwait(false);
                state = await ReadStateAsync(ct).ConfigureAwait(false);
                if (state.SubmittedSequence != sequence) continue;
                if (!state.IsTerminalState) continue;
                if (state.IsSuccess) return state;
                var detail = await TryGetCommandDetailAsync(state.ClientId, sequence, ct).ConfigureAwait(false);
                throw new ModbusCommandException(state.CommandState, state.ErrorCode,
                    DescribeError(state.CommandState, state.ErrorCode, detail), detail);
            }
            throw new TimeoutException($"命令 {opcode} 等待超时（{timeout.TotalSeconds:F0} 秒），序号 {sequence}。");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private uint NextSequence()
    {
        if (_sequence == uint.MaxValue)
            throw new InvalidOperationException("命令序号即将溢出，请断开并重新连接。");
        return ++_sequence;
    }

    private async Task StartHeartbeatAsync()
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;
        _heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var words = ModbusCodec.UInt32Words(++_heartbeatCounter);
                    await _modbus.WriteMultipleRegistersAsync(230, words, token).ConfigureAwait(false);
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                HeartbeatFailed?.Invoke(this, ex);
            }
        }, token);
    }

    private async Task StopHeartbeatAsync()
    {
        var cts = _heartbeatCts;
        var task = _heartbeatTask;
        _heartbeatCts = null;
        _heartbeatTask = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); } catch { }
        }
        cts.Dispose();
    }

    private async Task<string?> TryGetCommandDetailAsync(uint clientId, uint sequence, CancellationToken ct)
    {
        if (clientId == 0) return null;
        try
        {
            var detail = await _http.GetCommandAsync(clientId, sequence, ct).ConfigureAwait(false);
            return detail.GetRawText();
        }
        catch (Exception ex) when (ex is HttpRequestException or AxisLabHttpException or TaskCanceledException)
        {
            return null;
        }
    }

    public static string DescribeError(ushort state, ushort errorCode, string? detail = null)
    {
        var text = errorCode switch
        {
            0 => "无业务错误",
            1 => "未选择设备",
            2 => "工程会话已变化，请重新选择设备",
            3 => "设备配置已变化，请重新读取",
            4 => "命令序号无效",
            5 => "命令序号与旧命令冲突",
            6 => "命令序号过旧",
            7 => "不支持的命令",
            8 => "参数无效",
            9 => "所选设备不是机器人",
            10 => "所选设备不是相机",
            11 => "控制权被其他连接占用",
            12 => "当前连接未持有控制权",
            13 => "机器人正忙",
            14 => "关节超限",
            15 => "关节数量或数值无效",
            16 => "速度无效",
            17 => "渲染器未连接（请保持 AxisLab 窗口打开）",
            18 => "相机忙",
            19 => "拍照队列已满",
            20 => "拍照超时",
            21 => "渲染器断开",
            22 => "场景已变化，需重新拍照",
            23 => "拍照已取消",
            24 => "运动时间过长",
            25 => "设备不存在",
            26 => "图像超过像素上限",
            27 => "渲染失败",
            28 => "PNG 无效",
            29 => "拍照记录不存在",
            30 => "目标不可达",
            31 => "检测到碰撞",
            32 => "路径存在奇异问题",
            33 => "路径规划超出限制",
            34 => "示教点已失效",
            40 => "AxisLab 积木程序持有控制权，请先停止该程序",
            255 => "内部错误",
            _ => $"未知错误 {errorCode}"
        };
        var stateText = state switch
        {
            0 => "Idle",
            1 => "Accepted",
            2 => "Running",
            3 => "Completed",
            4 => "Failed",
            5 => "Cancelled",
            6 => "Rejected",
            _ => $"State={state}"
        };
        return detail is null ? $"{stateText} / {text}" : $"{stateText} / {text} / {detail}";
    }

    public async ValueTask DisposeAsync()
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        if (IsHoldingControl)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ExecuteCommandAsync(2, 0, null, 0, TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 断线时服务端会在 5 秒心跳超时后自动停止并撤销控制。
            }
            IsHoldingControl = false;
        }
        await _modbus.DisposeAsync().ConfigureAwait(false);
        _commandGate.Dispose();
    }
}



