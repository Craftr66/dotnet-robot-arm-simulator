using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AxisLab.VisionDemo
{
    public sealed class AxisClient : IDisposable
    {
        private readonly HttpClient http;
        public string Endpoint { get; }
        public AxisClient(string address)
        {
            var uri = new Uri(address.TrimEnd('/') + '/');
            if (uri.Scheme != "http") throw new ArgumentException("请输入 AxisLab 的 HTTP 地址。");
            Endpoint = uri.AbsoluteUri.TrimEnd('/'); http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
        }
        public async Task<T> Request<T>(string path, CancellationToken ct, object body = null)
        {
            using (var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, "api/" + path.TrimStart('/')))
            {
                if (body != null) request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AxisLab HTTP {(int)response.StatusCode}: {text}");
                    return Json.Read<T>(text);
                }
            }
        }
        public Task<DeviceInfo[]> Devices(CancellationToken ct) => Request<DeviceInfo[]>("v1/devices", ct);
        public Task<RuntimeInfo> Runtime(CancellationToken ct) => Request<RuntimeInfo>("v1/runtime", ct);
        public async Task<Frame> Capture(DeviceInfo camera, DeviceInfo robot, CancellationToken ct)
        {
            var before = await Runtime(ct);
            var devices = await Devices(ct);
            var selectedCamera = devices.Single(d => d.id == camera.id); var selectedRobot = devices.Single(d => d.id == robot.id);
            var capture = await Request<CaptureStatus>($"v1/cameras/{camera.id}/captures", ct, new { });
            var captureId = capture.captureId;
            var deadline = DateTime.UtcNow.AddSeconds(18);
            while (capture.status == "Queued" || capture.status == "Rendering")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("等待拍照超时；先查询已有 CaptureId，避免重复触发。" + captureId);
                await Task.Delay(100, ct); capture = await Request<CaptureStatus>("v1/captures/" + captureId, ct);
            }
            if (capture.status != "Completed" || string.IsNullOrEmpty(capture.frameId)) throw new InvalidOperationException(capture.errorCode + ": " + capture.message);
            var frame = await Request<FrameInfo>($"v1/cameras/{camera.id}/frames/{capture.frameId}", ct);
            if (frame.captureId != captureId || frame.frameId != capture.frameId || frame.cameraId != camera.id || frame.sessionId != before.sessionId || frame.cameraConfigurationRevision != selectedCamera.configurationRevision)
                throw new InvalidDataException("帧身份、会话或相机配置不匹配。");
            var imageUri = new Uri(http.BaseAddress, frame.imageUrl);
            if (imageUri.GetLeftPart(UriPartial.Authority) != http.BaseAddress.GetLeftPart(UriPartial.Authority)) throw new InvalidDataException("图像地址与 AxisLab 服务不一致。");
            byte[] png;
            using (var response = await http.GetAsync(imageUri, ct)) { response.EnsureSuccessStatusCode(); png = await response.Content.ReadAsByteArrayAsync(); }
            if (Json.Hash(png) != frame.sha256) throw new InvalidDataException("固定帧 PNG SHA-256 校验失败。");
            var after = await Runtime(ct); var afterDevices = await Devices(ct);
            if (after.sessionId != frame.sessionId || afterDevices.Single(d => d.id == camera.id).configurationRevision != selectedCamera.configurationRevision ||
                afterDevices.Single(d => d.id == robot.id).configurationRevision != selectedRobot.configurationRevision) throw new InvalidOperationException("取图期间设备或工程变化，请重新拍照。");
            var parent = selectedCamera.parentId; var attached = false;
            for (var i = 0; parent != null && i < devices.Length; i++)
            {
                var ancestor = devices.Single(d => d.id == parent); if (ancestor.typeId.StartsWith("robot.")) attached = true; parent = ancestor.parentId;
            }
            return new Frame { Info = frame, Png = png, Robot = frame.robots.Single(r => r.deviceId == robot.id), ReceivedUtc = DateTime.UtcNow,
                RobotConfiguration = selectedRobot.configurationRevision, MovingCamera = attached, Endpoint = Endpoint };
        }
        public async Task Stop(string robotId) => await Request<object>($"v1/robots/{robotId}/stop", CancellationToken.None, new { });
        public void Dispose() => http.Dispose();
    }
}
