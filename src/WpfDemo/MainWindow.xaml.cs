using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AxisLab.WpfDemo;

public partial class MainWindow : Window
{
    private static readonly Brush GrayBrush = Frozen(0x9C, 0xA3, 0xAF);
    private static readonly Brush GreenBrush = Frozen(0x16, 0xA3, 0x4A);

    private static readonly Brush BadgeGrayBg = Frozen(0xEE, 0xF2, 0xF7);
    private static readonly Brush BadgeGreenBg = Frozen(0xDC, 0xFC, 0xE7);
    private static readonly Brush BadgeRedBg = Frozen(0xFE, 0xE2, 0xE2);
    private static readonly Brush BadgeBlueBg = Frozen(0xE8, 0xF0, 0xFF);
    private static readonly Brush BadgeGreenFg = Frozen(0x15, 0x80, 0x3D);
    private static readonly Brush BadgeRedFg = Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush BadgeBlueFg = Frozen(0x1D, 0x4E, 0xD8);

    private readonly ObservableCollection<DeviceInfo> _devices = [];
    private readonly ObservableCollection<ProjectListEntry> _projects = [];
    private readonly ObservableCollection<JointRow> _joints = [];

    private AxisLabConnection? _connection;
    private CancellationTokenSource? _pollCts;
    private DeviceInfo? _selectedRobot;
    private DeviceInfo? _selectedCamera;
    private CapturedFrame? _lastFrame;
    private bool _isBusy;
    private bool _isMoving;
    private bool _isCapturing;
    private bool _suppressSelection;
    private bool _allowClose;
    private bool _closing;
    private bool _revokeInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        ProjectCombo.ItemsSource = _projects;
        JointPanel.ItemsSource = _joints;
        InitializeVision();

        var detected = AxisLabLocator.FindLastServiceUrl();
        if (detected is not null)
        {
            AddressBox.Text = detected;
            Log($"已从 desktop.log 检测到上次 HTTP 地址：{detected}");
        }
        else
        {
            Log("未找到 desktop.log 地址。请先启动 AxisLab，再点击“自动检测”或手工填写“外部连接”中的 HTTP 地址。");
        }
        Log("提示：HTTP 端口每次启动可能变化；Modbus 默认 127.0.0.1:1502，Unit ID 1。");
        UpdateUiState();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.Text.Length > 250_000)
        {
            LogBox.Text = LogBox.Text[^120_000..];
            LogBox.CaretIndex = LogBox.Text.Length;
        }
        LogBox.ScrollToEnd();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetBusy(bool value)
    {
        _isBusy = value;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var connected = _connection?.IsHttpConnected == true;
        var modbus = _connection?.IsModbusConnected == true;
        var session = _connection?.RobotSession;
        var holding = session?.IsHoldingControl == true;
        var robotReady = session?.IsSelected == true;

        AutoDetectButton.IsEnabled = !_isBusy;
        ConnectButton.IsEnabled = !connected && !_isBusy;
        DisconnectButton.IsEnabled = connected && !_isBusy;
        RefreshButton.IsEnabled = connected && !_isBusy && !_isMoving;
        ImportButton.IsEnabled = connected && !_isBusy && !_isMoving && !holding;
        OpenProjectButton.IsEnabled = connected && !_isBusy && !_isMoving && !holding && _projects.Count > 0;
        ProjectCombo.IsEnabled = connected && !_isBusy && !_isMoving && !holding;
        DeviceList.IsEnabled = connected && !_isBusy;

        AcquireButton.IsEnabled = connected && modbus && robotReady && !holding && !_isMoving;
        MoveButton.IsEnabled = connected && modbus && robotReady && holding && !_isMoving;
        ZeroButton.IsEnabled = connected && modbus && robotReady && holding && !_isMoving;
        ReleaseButton.IsEnabled = connected && modbus && robotReady && holding && !_isMoving;
        StopButton.IsEnabled = connected && modbus && robotReady && !_isBusy;

        CaptureButton.IsEnabled = connected && modbus && _selectedCamera is not null && !_isCapturing;
        SaveButton.IsEnabled = _lastFrame is not null;
        UpdateVisionUi();
    }
    private async void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var url = AxisLabLocator.FindLastServiceUrl();
        if (url is null)
        {
            Log("自动检测失败：未在 desktop.log 中找到 ServiceReady 地址。");
            MessageBox.Show(this, "未找到 AxisLab 的启动日志地址。请确认 AxisLab 已启动，或手工填写“外部连接”窗口中的 HTTP 地址。", "自动检测", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddressBox.Text = url;
        Log($"自动检测到 AxisLab：{url}");
        SetStatus("已填入检测地址");
        await Task.CompletedTask;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        SetStatus("正在连接...");
        try
        {
            var uri = ParseAddress();
            await DisconnectCoreAsync();
            var connection = new AxisLabConnection();
            await connection.ConnectAsync(uri);
            _connection = connection;
            AddressBox.Text = uri.GetLeftPart(UriPartial.Authority);

            ConnectionDot.Fill = GreenBrush;
            ConnectionText.Text = "已连接";
            Log($"已连接 AxisLab {_connection.Health?.Version}（{uri.GetLeftPart(UriPartial.Authority)}）。");
            Log($"HTTP 端口 {_connection.Communication?.HttpPort}，Modbus TCP 端口 {_connection.Communication?.ModbusPort}，Unit ID {_connection.Communication?.UnitId}。");
            if (_connection.IsModbusConnected)
                Log("Modbus 设备发现连接已建立。");
            else
                Log("警告：" + (_connection.ModbusError ?? "Modbus 未连接，机器人控制与拍照不可用。"));

            await LoadProjectsAsync();
            ApplyProjectToUi();
            ResetSelectedDevices();
            StartPolling();
            SetStatus("连接成功");
        }
        catch (Exception ex)
        {
            _connection = null;
            ConnectionDot.Fill = GrayBrush;
            ConnectionText.Text = "连接失败";
            Log($"连接失败：{ex.Message}");
            SetStatus("连接失败");
            MessageBox.Show(this, ex.Message, "连接 AxisLab 失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Uri ParseAddress()
    {
        var text = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请填写 AxisLab 的 HTTP 地址。");
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "http://" + text;
        if (!Uri.TryCreate(text.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException("地址格式无效，例如 http://127.0.0.1:53120。");
        return uri;
    }

    private async Task LoadProjectsAsync()
    {
        if (_connection is null) return;
        var list = await _connection.Http.GetProjectsAsync();
        _projects.Clear();
        foreach (var item in list) _projects.Add(item);
        if (_projects.Count > 0 && ProjectCombo.SelectedIndex < 0)
            ProjectCombo.SelectedIndex = 0;
        if (_projects.Count == 0)
            Log("工程目录中没有已保存的工程；可用“导入 JSON...”载入 Samples/external-control.scene.json。");
        else
            Log($"已读取 {_projects.Count} 个已保存工程。");
        UpdateUiState();
    }

    private void ApplyProjectToUi()
    {
        var project = _connection?.Project;
        if (project is null)
        {
            ProjectNameText.Text = "未连接";
            ProjectMetaText.Text = "连接后显示工程会话、版本和设备数量";
            DeviceCountText.Text = "工程设备";
            SessionText.Text = "项目会话：—";
            _devices.Clear();
            _joints.Clear();
            UpdateUiState();
            return;
        }

        ProjectNameText.Text = project.Project.Name;
        ProjectMetaText.Text =
            $"schema v{project.Project.SchemaVersion} · 修订 {project.Revision} · " +
            $"{(project.Dirty ? "有未保存修改" : "已保存")} · {project.Project.Devices.Count} 台设备";
        SessionText.Text = $"项目会话：{project.SessionId.ToString()[..8]} · Modbus {_connection?.Communication?.ModbusPort}";

        _devices.Clear();
        foreach (var device in _connection!.Devices) _devices.Add(device);
        DeviceCountText.Text = $"工程设备（{_devices.Count}）";
        Log($"当前工程“{project.Project.Name}”，会话 {project.SessionId}，修订 {project.Revision}，设备 {_devices.Count} 台。");
    }

    private void ResetSelectedDevices()
    {
        ResetVision(true);
        _selectedRobot = null;
        _selectedCamera = null;
        _lastFrame = null;
        _joints.Clear();
        RobotTitleText.Text = "未选择机器人";
        RobotMetaText.Text = "从左侧设备列表选择三轴或六轴机械臂";
        CameraTitleText.Text = "未选择相机";
        CameraMetaText.Text = "从左侧设备列表选择针孔相机";
        CameraImage.Source = null;
        CameraStatusText.Text = "尚未拍照";
        FrameInfoText.Text = "";
        RobotProgress.Value = 0;
        ProgressText.Text = "0%";
        TcpText.Text = "—";
        RobotDetailsText.Text = "";
        SetBadge(ControlBadge, ControlBadgeText, "控制权：—", BadgeGrayBg, (Brush)FindResource("MutedBrush"));
        SetBadge(MotionBadge, MotionBadgeText, "运动：Idle", BadgeGrayBg, (Brush)FindResource("MutedBrush"));
        _suppressSelection = true;
        DeviceList.SelectedItem = null;
        _suppressSelection = false;
        UpdateUiState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _isBusy) return;
        SetBusy(true);
        try
        {
            var previousSession = _connection.Project?.SessionId;
            var robotNumber = _selectedRobot?.Number;
            var cameraNumber = _selectedCamera?.Number;
            await _connection.RefreshProjectAsync();
            ApplyProjectToUi();
            if (previousSession != _connection.Project?.SessionId)
            {
                ResetSelectedDevices();
                Log("工程会话已变化，设备选择已清空，请重新选择。");
            }
            else
            {
                RebindSelection(robotNumber, cameraNumber);
            }
            await LoadProjectsAsync();
            Log("已刷新当前工程与设备列表。");
            SetStatus("工程已刷新");
        }
        catch (Exception ex)
        {
            Log($"刷新失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "刷新工程失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebindSelection(ushort? robotNumber, ushort? cameraNumber)
    {
        _suppressSelection = true;
        try
        {
            if (robotNumber is not null && _selectedRobot is not null)
            {
                var match = _devices.FirstOrDefault(d => d.Number == robotNumber);
                if (match is not null) DeviceList.SelectedItem = match;
            }
            else if (cameraNumber is not null && _selectedCamera is not null)
            {
                var match = _devices.FirstOrDefault(d => d.Number == cameraNumber);
                if (match is not null) DeviceList.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }
    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _isBusy) return;
        if (_connection.RobotSession?.IsHoldingControl == true)
        {
            MessageBox.Show(this, "当前仍持有机器人控制权。请先“释放控制”或“停止”，再切换工程。", "外部控制中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ProjectCombo.SelectedItem is not ProjectListEntry entry)
        {
            MessageBox.Show(this, "请先在“已保存工程”下拉框中选择工程。", "选择工程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_connection.Project?.Dirty == true &&
            MessageBox.Show(this, "当前工程有未保存修改，打开其他工程会丢失这些修改。继续？", "确认打开工程",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            await _connection.OpenProjectAsync(entry.Id);
            ApplyProjectToUi();
            ResetSelectedDevices();
            await LoadProjectsAsync();
            Log($"已打开工程“{entry.Name}”。新会话已生效，需要重新选择设备。");
            SetStatus("工程已打开");
        }
        catch (Exception ex)
        {
            Log($"打开工程失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "打开工程失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _isBusy) return;
        if (_connection.RobotSession?.IsHoldingControl == true)
        {
            MessageBox.Show(this, "当前仍持有机器人控制权。请先释放控制，再导入工程。", "外部控制中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_connection.Project?.Dirty == true &&
            MessageBox.Show(this, "当前工程有未保存修改，导入其他工程会丢失这些修改。继续？", "确认导入工程",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "导入 AxisLab 工程 JSON",
            Filter = "AxisLab 工程 (*.scene.json;*.json)|*.scene.json;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true);
        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("工程文件顶层必须是 JSON 对象。");
            await _connection.ImportProjectAsync(doc.RootElement.Clone());
            ApplyProjectToUi();
            ResetSelectedDevices();
            await LoadProjectsAsync();
            Log($"已导入工程文件：{dialog.FileName}。新会话已生效，需要重新选择设备。");
            SetStatus("工程已导入");
        }
        catch (Exception ex)
        {
            Log($"导入工程失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "导入工程失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _connection is null) return;
        if (DeviceList.SelectedItem is not DeviceInfo device) return;
        if (_visionBusy) return;

        if (device.IsRobot)
        {
            var active = _connection.RobotSession;
            if (active?.IsHoldingControl == true && active.SelectedNumber != device.Number)
            {
                MessageBox.Show(this, "当前机器人仍被本连接接管。请先“释放控制”或“停止”，再选择其他设备。", "外部控制中", MessageBoxButton.OK, MessageBoxImage.Information);
                _suppressSelection = true;
                DeviceList.SelectedItem = _selectedRobot;
                _suppressSelection = false;
                return;
            }

            SetBusy(true);
            try
            {
                _selectedRobot = device;
                ResetVision(true);
                BuildJointRows(device);
                var state = await _connection.SelectRobotAsync(device);
                AttachSessionEvents(_connection.RobotSession);
                RobotTitleText.Text = $"#{device.Number} {device.Name}";
                RobotMetaText.Text = $"{device.TypeName} · {device.Axes} 轴 · UUID {device.Id}";
                ApplyRobotState(state);
                Log($"已选择机器人 #{device.Number} {device.Name}（{device.Axes} 轴），Modbus 身份有效。");
                SetStatus($"已选择机器人 #{device.Number}");
            }
            catch (Exception ex)
            {
                _selectedRobot = null;
                _joints.Clear();
                RobotTitleText.Text = "选择机器人失败";
                RobotMetaText.Text = device.Name;
                Log($"选择机器人 #{device.Number} 失败：{ex.Message}");
                MessageBox.Show(this, ex.Message, "选择机器人失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        if (device.IsCamera)
        {
            if (_selectedCamera?.Id != device.Id) { ResetVision(true); _lastFrame = null; CameraImage.Source = null; }
            _selectedCamera = device;
            CameraTitleText.Text = $"#{device.Number} {device.Name}";
            CameraMetaText.Text = $"{device.TypeName} · {device.Parameters.Width}×{device.Parameters.Height} px · UUID {device.Id}";
            CameraStatusText.Text = "相机已选择，点击“拍照并取图”。";
            Log($"已选择相机 #{device.Number} {device.Name}。");
            SetStatus($"已选择相机 #{device.Number}");
            UpdateUiState();
            return;
        }

        Log($"设备 #{device.Number} {device.Name} 是{device.TypeName}，不支持外部运动或拍照。");
        SetStatus("该设备只支持工程编辑");
        await Task.CompletedTask;
    }

    private void AttachSessionEvents(ExternalControlSession? session)
    {
        if (session is null) return;
        session.HeartbeatFailed += (_, ex) => Dispatcher.BeginInvoke(async () =>
        {
            Log($"控制心跳失败：{ex.Message}。服务端将在 5 秒无心跳后自动停止并撤销控制。");
            SetStatus("心跳失败");
            await session.NotifyControlRevokedAsync();
            UpdateUiState();
        });
    }
    private void BuildJointRows(DeviceInfo device)
    {
        _joints.Clear();
        var model = _connection?.RobotModels.FirstOrDefault(m => m.ModelId == device.Robot?.ModelId);
        var current = device.Robot?.JointPositions ?? [];
        var count = device.Axes is 3 or 6 ? device.Axes : (ushort)current.Length;
        for (var i = 0; i < count; i++)
        {
            var definition = model?.Joints.ElementAtOrDefault(i);
            _joints.Add(new JointRow
            {
                Name = definition?.Name ?? $"J{i + 1}",
                Minimum = definition?.Minimum ?? -180,
                Maximum = definition?.Maximum ?? 180,
                MaxVelocity = definition?.MaxVelocity ?? 90,
                Target = i < current.Length ? current[i] : 0,
                Current = i < current.Length ? current[i] : 0
            });
        }
        RobotProgress.Value = 0;
        ProgressText.Text = "0%";
    }

    private void ApplyRobotState(DeviceState state)
    {
        if (_selectedRobot is null) return;
        if (state.SelectedDeviceNumber != 0 && state.SelectedDeviceNumber != _selectedRobot.Number) return;

        for (var i = 0; i < _joints.Count && i < state.Axes && i < state.Joints.Length; i++)
            _joints[i].Current = state.Joints[i];

        RobotProgress.Value = Math.Clamp(state.ProgressPercent, 0, 100);
        ProgressText.Text = $"{state.ProgressPercent:F0}%";

        var motionText = state.MotionStatus switch
        {
            0 => ("运动：Idle", BadgeGrayBg, (Brush)FindResource("MutedBrush")),
            1 => ("运动：Running", BadgeBlueBg, BadgeBlueFg),
            2 => ("运动：Completed", BadgeGreenBg, BadgeGreenFg),
            3 => ("运动：Cancelled", BadgeGrayBg, (Brush)FindResource("WarnBrush")),
            _ => ($"运动：{state.MotionStatus}", BadgeGrayBg, (Brush)FindResource("MutedBrush"))
        };
        SetBadge(MotionBadge, MotionBadgeText, motionText.Item1, motionText.Item2, motionText.Item3);

        if (state.IsOwnedByThisConnection)
        {
            SetBadge(ControlBadge, ControlBadgeText, "控制权：本连接持有", BadgeGreenBg, BadgeGreenFg);
            BoundaryText.Text = "已接管：AxisLab 本地点动、场景编辑与保存被锁定。请保持心跳；释放、停止或断开后恢复本地控制。";
            BoundaryText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else if (state.IsOwnedByOther)
        {
            SetBadge(ControlBadge, ControlBadgeText, "控制权：其他连接持有", BadgeRedBg, BadgeRedFg);
            BoundaryText.Text = "控制权由其他连接持有；本机只能读取状态，不能运动。";
            BoundaryText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else
        {
            SetBadge(ControlBadge, ControlBadgeText, "控制权：空闲", BadgeGrayBg, (Brush)FindResource("MutedBrush"));
            BoundaryText.Text = "控制权空闲：AxisLab 本地可以点动和编辑。点击“接管控制”后才会锁定本地操作。";
            BoundaryText.Foreground = (Brush)FindResource("WarnBrush");
        }

        if (!_revokeInProgress && _connection?.RobotSession is { IsHoldingControl: true } session && state.ControlOwner != 1)
        {
            _revokeInProgress = true;
            _ = NotifyRevokedAsync(session, state.ControlOwner);
        }

        RobotDetailsText.Text =
            $"类型 {AxisLabTypes.TypeName(state.SelectedType)} · 轴数 {state.Axes} · 配置版本 {state.ConfigurationRevision} · " +
            $"ClientId {state.ClientId} · 心跳剩余 {state.HeartbeatRemainingMs} ms · 选择有效 {(state.IdentityValid ? "是" : "否")}";
        TcpText.Text =
            $"X {state.TcpBase[0],9:F2}  Y {state.TcpBase[1],9:F2}  Z {state.TcpBase[2],9:F2}{Environment.NewLine}" +
            $"Rx {state.TcpBase[3],8:F2}  Ry {state.TcpBase[4],8:F2}  Rz {state.TcpBase[5],8:F2}";

        if (!state.IdentityValid)
        {
            SetStatus("当前选择已失效，请刷新工程并重新选择设备");
            BoundaryText.Text = "当前选择已失效（工程会话或配置变化）。请重新选择设备。";
            BoundaryText.Foreground = (Brush)FindResource("DangerBrush");
        }

        UpdateUiState();
    }

    private async Task NotifyRevokedAsync(ExternalControlSession session, ushort owner)
    {
        try
        {
            await session.NotifyControlRevokedAsync();
            Log(owner == 2 ? "控制权已被其他连接接管，本机心跳已停止。" : "控制权已被撤销（可能是 AxisLab 本地停止或心跳超时）。");
            UpdateUiState();
        }
        finally
        {
            _revokeInProgress = false;
        }
    }

    private static void SetBadge(Border border, TextBlock text, string content, Brush background, Brush foreground)
    {
        border.Background = background;
        text.Text = content;
        text.Foreground = foreground;
    }
    private async void Acquire_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _selectedRobot is null || _isBusy) return;
        SetBusy(true);
        SetStatus("正在申请控制权...");
        try
        {
            var state = await _connection.AcquireRobotControlAsync();
            ApplyRobotState(state);
            Log($"已接管机器人 #{_selectedRobot.Number}。AxisLab 本地运动与编辑已锁定；开始每秒心跳。");
            SetStatus("已接管控制权");
        }
        catch (Exception ex)
        {
            Log($"接管失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "接管控制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("接管失败");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Release_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _isBusy) return;
        SetBusy(true);
        try
        {
            var state = await _connection.ReleaseRobotControlAsync();
            ApplyRobotState(state);
            Log("已释放机器人控制权，AxisLab 本地操作恢复。");
            SetStatus("已释放控制权");
        }
        catch (Exception ex)
        {
            Log($"释放控制失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "释放控制失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Move_Click(object sender, RoutedEventArgs e) => await MoveToTargetsAsync("运动");

    private async void Zero_Click(object sender, RoutedEventArgs e)
    {
        foreach (var joint in _joints) joint.Target = 0;
        await MoveToTargetsAsync("回零");
    }

    private async Task MoveToTargetsAsync(string action)
    {
        if (_connection is null || _selectedRobot is null || _isMoving) return;
        if (_connection.RobotSession?.IsHoldingControl != true)
        {
            MessageBox.Show(this, "尚未持有机器人控制权。请先点击“接管控制”。", "需要控制权", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_joints.Count == 0)
        {
            MessageBox.Show(this, "当前机器人没有可用的关节目标。", "无关节", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = _joints.Select(j => j.Target).ToArray();
        var speed = SpeedSlider.Value;
        _isMoving = true;
        UpdateUiState();
        SetStatus($"{action}中...");
        Log($"{action}：目标 [{string.Join(", ", targets.Select(v => v.ToString("F2")))}]°，速度 {speed:F0}%。");
        try
        {
            var state = await _connection.MoveRobotAsync(targets, speed);
            ApplyRobotState(state);
            Log($"{action}完成。AxisLab 仿真窗口已按关节插补实时运动。");
            SetStatus($"{action}完成");
        }
        catch (ModbusCommandException ex) when (ex.CommandState == 5)
        {
            Log($"{action}被停止或取消：{ex.Message}");
            SetStatus($"{action}已停止");
            try { ApplyRobotState(await _connection.ReadRobotStateAsync()); } catch { }
        }
        catch (Exception ex)
        {
            Log($"{action}失败：{ex.Message}");
            SetStatus($"{action}失败");
            MessageBox.Show(this, ex.Message, $"{action}失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isMoving = false;
            UpdateUiState();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _vmCts?.Cancel();
        ClearVisionResult();
        if (_connection is null || _selectedRobot is null || _isBusy) return;
        SetBusy(true);
        try
        {
            var state = await _connection.StopRobotAsync();
            ApplyRobotState(state);
            Log("已发送停止指令；运动取消，外部控制权已撤销，AxisLab 本地操作恢复。");
            SetStatus("已停止");
        }
        catch (Exception ex)
        {
            Log($"停止失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText is not null) SpeedText.Text = $"{e.NewValue:F0}%";
    }
    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null || _selectedCamera is null || _isCapturing) return;
        _isCapturing = true;
        ResetVision(false);
        CaptureButton.Content = "拍照中...";
        UpdateUiState();
        SetStatus("正在拍照...");
        try
        {
            var frame = await _connection.CaptureAsync(_selectedCamera);
            _lastFrame = frame;
            DisplayFrame(frame);
            await PrepareVisionFrame(frame);
            Log($"拍照成功：{frame.Width}×{frame.Height}，frameToken={frame.FrameToken}，SHA-256 {frame.Sha256}。");

            if (AutoSaveCheckBox.IsChecked == true)
            {
                var path = await SaveFrameToFolderAsync(frame);
                Log($"已自动保存：{path}");
            }
            SetStatus("拍照完成");
        }
        catch (Exception ex)
        {
            Log($"拍照失败：{ex.Message}");
            CameraStatusText.Text = "拍照失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "拍照失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("拍照失败");
        }
        finally
        {
            _isCapturing = false;
            CaptureButton.Content = "拍照并取图";
            UpdateUiState();
        }
    }

    private void DisplayFrame(CapturedFrame frame)
    {
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(frame.Png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        CameraImage.Source = bitmap;
        CameraStatusText.Text =
            $"{frame.Width}×{frame.Height} px · {frame.Png.Length / 1024.0:F1} KiB · " +
            $"frame {frame.FrameId.ToString()[..8]} · SHA-256 {frame.Sha256[..16]}...";
        FrameInfoText.Text = PrettyJson(frame.MetadataJson);
        SaveButton.IsEnabled = true;
    }

    private static string PrettyJson(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, JsonDefaults.Web);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string CapturesDirectory
    {
        get
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("AXISLAB_WPF_DATA") ?? AppContext.BaseDirectory, "captures");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static async Task<string> SaveFrameToFolderAsync(CapturedFrame frame)
    {
        var pngPath = Path.Combine(CapturesDirectory, frame.FrameId.ToString("D") + ".png");
        await File.WriteAllBytesAsync(pngPath, frame.Png);
        await File.WriteAllTextAsync(Path.ChangeExtension(pngPath, ".frame.json"), PrettyJson(frame.MetadataJson));
        return pngPath;
    }

    private async void SaveFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFrame is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "保存拍照结果",
            Filter = "PNG 图像 (*.png)|*.png",
            FileName = _lastFrame.FrameId.ToString("D") + ".png",
            InitialDirectory = CapturesDirectory
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, _lastFrame.Png);
            var jsonPath = Path.ChangeExtension(dialog.FileName, ".frame.json");
            await File.WriteAllTextAsync(jsonPath, PrettyJson(_lastFrame.MetadataJson));
            Log($"已保存 PNG 和帧 JSON：{dialog.FileName}");
            SetStatus("拍照结果已保存");
        }
        catch (Exception ex)
        {
            Log($"保存失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = CapturesDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"打开输出目录失败：{ex.Message}");
        }
    }

    private void StartPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = new CancellationTokenSource();
        _ = PollRobotLoopAsync(_pollCts.Token);
    }

    private async Task PollRobotLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var session = _connection?.RobotSession;
            if (session is { IsSelected: true })
            {
                try
                {
                    var state = await session.ReadStateAsync(ct);
                    await Dispatcher.InvokeAsync(() => ApplyRobotState(state));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or InvalidDataException or System.Net.Sockets.SocketException)
                {
                    await Dispatcher.InvokeAsync(() => SetStatus("状态轮询失败：" + ex.Message));
                    try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                }
            }

            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await DisconnectCoreAsync();
            SetStatus("已断开");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await CloseVision();
        if (_connection?.RobotSession?.IsHoldingControl == true)
            try { await _connection.StopRobotAsync(); } catch (Exception ex) { Log("断开前停止：" + ex.Message); }
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;

        if (_connection is not null)
        {
            try
            {
                await _connection.DisconnectAsync();
                Log("已断开 AxisLab；机器人控制权已释放（若仍持有）。");
            }
            catch (Exception ex)
            {
                Log($"断开时释放资源失败：{ex.Message}");
            }
            await _connection.DisposeAsync();
            _connection = null;
        }

        ConnectionDot.Fill = GrayBrush;
        ConnectionText.Text = "未连接";
        ProjectNameText.Text = "未连接";
        ProjectMetaText.Text = "连接后显示工程会话、版本和设备数量";
        DeviceCountText.Text = "工程设备";
        SessionText.Text = "项目会话：—";
        _devices.Clear();
        _projects.Clear();
        ResetSelectedDevices();
        SetStatus("未连接");
        UpdateUiState();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        Log("正在断开连接并释放控制权...");
        try
        {
            await DisconnectCoreAsync();
        }
        catch (Exception ex)
        {
            Log($"退出清理失败：{ex.Message}");
        }
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}





