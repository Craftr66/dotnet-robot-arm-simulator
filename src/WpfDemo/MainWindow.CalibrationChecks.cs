using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AxisLab.WpfDemo;

public partial class MainWindow
{
    public async Task RunSamplingChecks(string settingsFile, string reportFile)
    {
        var config = JsonSerializer.Deserialize<FrameworkCheckSettings>(File.ReadAllText(settingsFile), JsonDefaults.Web)!;
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); Log("PASS " + name); }
        async Task Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(LogBox.Text[^Math.Min(LogBox.Text.Length, 1500)..]); await Task.Delay(50); }
        }
        AddressBox.Text = config.Url; ConnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !_isBusy && _connection?.IsHttpConnected == true);
        var robot = _devices.Single(d => d.Axes == 6); DeviceList.SelectedItem = robot;
        await Wait(() => !_isBusy && _connection!.RobotSession?.IsSelected == true);
        DeviceList.SelectedItem = _devices.Single(d => d.IsCamera);
        await Wait(() => _selectedCamera is not null);
        Check(_devices.Any(d => d.Type == AxisLabTypes.CalibrationMarker), "new TCP marker is discovered over HTTP and Modbus");
        var runtime = await _connection!.Http.GetRuntimeAsync(); var p = runtime.Robots.Single().TcpBase.Pose;
        foreach (var pair in new[] { (SampleX,p.X), (SampleY,p.Y), (SampleZ,p.Z), (SampleRx,p.Rx), (SampleRy,p.Ry), (SampleRz,p.Rz) }) pair.Item1.Text = pair.Item2.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        SampleSpacing.Text = "20"; VmSampleDirectory.Text = Path.Combine(config.Directory, "samples");
        await GenerateSampling(CancellationToken.None);
        Check(_samplingPlan!.Points.Count == 12 && (await _connection.Http.GetRuntimeAsync()).Robots.Single().PoseId == runtime.Robots.Single().PoseId, "generating a nine-point plan does not move the robot");
        var premature = false; try { await CaptureSampling(CancellationToken.None); } catch (InvalidOperationException) { premature = true; }
        Check(premature && _samplingPlan.Points[0].FrameId is null, "capture rejects the wrong robot pose instead of pairing it with a planned coordinate");
        foreach (var point in _samplingPlan.Points)
        {
            SamplingGrid.SelectedItem = point;
            await MoveSampling(CancellationToken.None); await CaptureSampling(CancellationToken.None);
            Check(point.FrameId is not null && point.Actual is not null && File.Exists(Path.Combine(_samplingPlan.Directory, point.Image!)), $"point {point.Number:D2} moves, stops and saves a real rendered PNG with exposure TCP");
            var observation = JsonDocument.Parse(File.ReadAllText(Path.Combine(_samplingPlan.Directory, Path.GetFileNameWithoutExtension(point.Image) + ".observation.json")));
            Check(observation.RootElement.GetProperty("image").GetString() == point.Image && !observation.RootElement.TryGetProperty("cameraWorld", out _), $"point {point.Number:D2} observation names the exact PNG and excludes hidden camera installation");
        }
        _samplingPlan.Export();
        Check(File.ReadAllLines(Path.Combine(_samplingPlan.Directory, "points.csv")).Length == 13, "all nine fitting rows and three independent validation rows export together");
        Check(_connection.RobotSession?.IsHoldingControl == false, "sampling releases control that it acquired automatically");
        var noReport = false; try { _samplingPlan.Report(.5); } catch (InvalidOperationException) { noReport = true; }
        Check(noReport, "no accuracy report is invented before actual VM verification");
        SamplingTab.IsSelected = true;
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var image = File.Create(Path.Combine(config.Directory, "wpf-nine-point.png"))) encoder.Save(image);
        File.WriteAllText(reportFile, JsonSerializer.Serialize(new { success = true, checks, samplingDirectory = _samplingPlan.Directory }, JsonDefaults.Web));
    }
}
