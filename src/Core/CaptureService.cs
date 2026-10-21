using System.Security.Cryptography;

namespace AxisLab.Core;

public record CaptureScene(Guid SessionId, long SceneRevision, Device Camera, ProjectDocument Project, RuntimeSnapshot Runtime);
public record RendererLease(Guid RendererId, string Token, int LeaseSeconds = 4);
public record CaptureWork(Guid CaptureId, DateTimeOffset ExposureAt, CaptureScene Snapshot);
public record CaptureState(Guid CaptureId, Guid CameraId, string Status, Guid? FrameId, string? ErrorCode, string? Message);
public record FrameMetadata(Guid FrameId, Guid CaptureId, Guid CameraId, string CameraName, Guid SessionId,
    long SceneRevision, long CameraConfigurationRevision, DateTimeOffset ExposureAt, DateTimeOffset CompletedAt,
    int Width, int Height, string Encoding, string Sha256, CoordinateFrame CameraWorld,
    DeviceParameters Intrinsics, RobotState[] Robots, string ImageUrl);
public record FrameImage(FrameMetadata Metadata, byte[] Bytes);

public sealed partial class ProjectStore
{
    public CaptureScene FreezeCamera(Guid cameraId, Guid? expectedSession = null, long? expectedConfiguration = null)
    {
        lock (gate)
        {
            ExpireControls();
            var camera = Find(cameraId);
            if (expectedSession.HasValue && expectedSession != sessionId) throw new DomainException("SessionChanged", "工程会话已变化。", 409);
            if (expectedConfiguration.HasValue && expectedConfiguration != camera.ConfigurationRevision) throw new DomainException("ConfigurationChanged", "相机配置已变化。", 409);
            if (camera.TypeId != "camera.pinhole") throw new DomainException("NotCamera", "此对象不是相机。");
            return new(sessionId, revision, camera, WithJointPositions(), BuildRuntime());
        }
    }
    public bool IsCaptureSceneCurrent(CaptureScene scene)
    {
        lock (gate) return scene.SessionId == sessionId && scene.Project.Id == project.Id &&
            scene.Project.Devices.Length == project.Devices.Length && scene.Project.Devices.All(old =>
                project.Devices.Any(d => d.Id == old.Id && d.ConfigurationRevision == old.ConfigurationRevision));
    }
    public Guid SessionId { get { lock (gate) return sessionId; } }
}

/// <summary>A single leased renderer consumes frozen scenes. All completion paths bind back to their capture.</summary>
public sealed class CaptureService(ProjectStore projects, TimeProvider? timeProvider = null,
    int framesPerCamera = 20, long maximumCacheBytes = 256L * 1024 * 1024)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private RendererLease? renderer;
    private long rendererHeartbeat;
    private readonly Dictionary<Guid, Job> jobs = [];
    private readonly Dictionary<Guid, Stored> frames = [];
    private readonly Queue<Guid> frameOrder = [];
    private long cacheBytes;
    private sealed class Job(CaptureWork work, Guid rendererId, long created)
    {
        public CaptureWork Work = work;
        public Guid RendererId = rendererId;
        public long Created = created;
        public string Status = "Queued";
        public Guid? FrameId;
        public string? Error;
        public string? Message;
        public CaptureState State => new(Work.CaptureId, Work.Snapshot.Camera.Id, Status, FrameId, Error, Message);
    }
    private sealed class Stored(FrameMetadata metadata, byte[] bytes)
    {
        public FrameMetadata Metadata = metadata;
        public byte[]? Bytes = bytes;
    }
    private static bool Active(Job j) => j.Status is "Queued" or "Rendering";
    private static void Fail(Job job, string error, string message)
    { job.Status = "Failed"; job.Error = error; job.Message = message; }
    private void Expire()
    {
        if (renderer is not null && clock.GetElapsedTime(rendererHeartbeat).TotalSeconds > renderer.LeaseSeconds)
        {
            foreach (var job in jobs.Values.Where(Active)) Fail(job, "RendererDisconnected", "渲染界面连接已中断。");
            renderer = null;
        }
        foreach (var job in jobs.Values.Where(Active))
        {
            if (!projects.IsCaptureSceneCurrent(job.Work.Snapshot)) Fail(job, "SceneChanged", "场景或相机配置已改变，请重新拍照。");
            else if (clock.GetElapsedTime(job.Created).TotalSeconds > 12) Fail(job, "CaptureTimeout", "相机成像超时，请重试。");
        }
    }
    private void Authorize(Guid id, string token)
    {
        Expire();
        if (renderer is null || renderer.RendererId != id || renderer.Token != token)
            throw new DomainException("RendererUnauthorized", "渲染会话无效或已过期。", 403);
    }
    public RendererLease Register()
    {
        lock (gate)
        {
            Expire();
            if (renderer is not null) throw new DomainException("RendererBusy", "已有渲染界面正在服务。", 409);
            renderer = new(Guid.NewGuid(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
            rendererHeartbeat = clock.GetTimestamp(); return renderer;
        }
    }
    public void Heartbeat(Guid id, string token) { lock (gate) { Authorize(id, token); rendererHeartbeat = clock.GetTimestamp(); } }
    public CaptureState Trigger(Guid cameraId, Guid? expectedSession = null, long? expectedConfiguration = null)
    {
        lock (gate)
        {
            Expire();
            var scene = projects.FreezeCamera(cameraId, expectedSession, expectedConfiguration);
            if (renderer is null) throw new DomainException("RendererUnavailable", "请先打开软件三维界面并等待渲染服务就绪。", 503);
            if ((long)scene.Camera.Parameters.Width * scene.Camera.Parameters.Height > 4_194_304)
                throw new DomainException("CaptureTooLarge", "单帧最多支持 4194304 像素，请降低分辨率。");
            if (jobs.Values.Any(j => Active(j) && j.Work.Snapshot.Camera.Id == cameraId))
                throw new DomainException("CameraBusy", "此相机正在拍照。", 409);
            if (jobs.Values.Count(Active) >= 8) throw new DomainException("CaptureQueueFull", "成像队列已满。", 409);
            while (jobs.Count >= 256)
            {
                var old = jobs.FirstOrDefault(p => !Active(p.Value));
                if (old.Value is null) break; jobs.Remove(old.Key);
            }
            var work = new CaptureWork(Guid.NewGuid(), clock.GetUtcNow(), scene);
            var job = new Job(work, renderer.RendererId, clock.GetTimestamp());
            jobs.Add(work.CaptureId, job); return job.State;
        }
    }
    public CaptureWork? TakeWork(Guid rendererId, string token)
    {
        lock (gate)
        {
            Authorize(rendererId, token);
            if (jobs.Values.Any(j => j.Status == "Rendering")) return null;
            var job = jobs.Values.FirstOrDefault(j => j.Status == "Queued");
            if (job is null) return null;
            job.Status = "Rendering"; return job.Work;
        }
    }
    private Job Find(Guid id) => jobs.GetValueOrDefault(id) ?? throw new DomainException("CaptureNotFound", "捕获记录不存在或已清理。", 404);
    public CaptureState Get(Guid id) { lock (gate) { Expire(); return Find(id).State; } }
    public void Cancel(Guid id)
    {
        lock (gate) { Expire(); var job = Find(id); if (Active(job)) Fail(job, "CaptureCancelled", "捕获已取消。"); }
    }
    public CaptureState RenderFailed(Guid rendererId, string token, Guid captureId, string message)
    {
        lock (gate)
        {
            Authorize(rendererId, token); var job = Find(captureId);
            if (job.RendererId != rendererId || job.Status != "Rendering") throw new DomainException("CaptureNotActive", "捕获已结束或不属于此界面。", 409);
            Fail(job, "RenderFailed", string.IsNullOrWhiteSpace(message) ? "相机渲染失败。" : message[..Math.Min(message.Length, 200)]);
            return job.State;
        }
    }
    public CaptureState Complete(Guid rendererId, string token, Guid captureId, byte[] png)
    {
        CaptureWork work;
        lock (gate)
        {
            Authorize(rendererId, token); var job = Find(captureId);
            if (job.Status != "Rendering" || job.RendererId != rendererId) throw new DomainException("CaptureNotActive", "捕获已结束或不属于此界面。", 409);
            work = job.Work;
        }
        try { PngValidation.Validate(png, work.Snapshot.Camera.Parameters.Width, work.Snapshot.Camera.Parameters.Height); }
        catch (DomainException)
        { lock (gate) { var bad = Find(captureId); if (Active(bad)) Fail(bad, "InvalidPng", "渲染图像无效。"); } throw; }
        lock (gate)
        {
            Authorize(rendererId, token); var job = Find(captureId);
            if (job.Status != "Rendering") throw new DomainException("CaptureNotActive", job.Message ?? "捕获已结束。", 409);
            var id = Guid.NewGuid(); var camera = work.Snapshot.Camera;
            var metadata = new FrameMetadata(id, captureId, camera.Id, camera.Name, work.Snapshot.SessionId,
                work.Snapshot.SceneRevision, camera.ConfigurationRevision, work.ExposureAt, clock.GetUtcNow(),
                camera.Parameters.Width, camera.Parameters.Height, "png", Convert.ToHexString(SHA256.HashData(png)),
                work.Snapshot.Runtime.Devices.Single(d => d.DeviceId == camera.Id).World,
                camera.Parameters, work.Snapshot.Runtime.Robots, $"/api/v1/cameras/{camera.Id}/frames/{id}/image");
            frames[id] = new(metadata, [.. png]); frameOrder.Enqueue(id); cacheBytes += png.Length;
            void Evict(Stored f) { if (f.Bytes is not null) { cacheBytes -= f.Bytes.Length; f.Bytes = null; } }
            while (frames.Values.Count(f => f.Bytes is not null && f.Metadata.CameraId == camera.Id) > framesPerCamera)
                Evict(frameOrder.Select(key => frames[key]).First(f => f.Bytes is not null && f.Metadata.CameraId == camera.Id));
            while (cacheBytes > maximumCacheBytes) Evict(frameOrder.Select(key => frames[key]).First(f => f.Bytes is not null));
            while (frames.Count > 512) { var key = frameOrder.Dequeue(); Evict(frames[key]); frames.Remove(key); }
            job.Status = "Completed"; job.FrameId = id; return job.State;
        }
    }
    private Stored Read(Guid cameraId, Guid frameId)
    {
        if (!frames.TryGetValue(frameId, out var f) || f.Metadata.CameraId != cameraId)
            throw new DomainException("FrameNotFound", "图像帧不存在。", 404);
        if (f.Metadata.SessionId != projects.SessionId || f.Bytes is null)
            throw new DomainException("FrameGone", "图像缓存已淘汰或工程会话已切换。", 410);
        return f;
    }
    public FrameMetadata Metadata(Guid cameraId, Guid frameId) { lock (gate) return Read(cameraId, frameId).Metadata; }
    public FrameImage Image(Guid cameraId, Guid frameId) { lock (gate) { var f = Read(cameraId, frameId); return new(f.Metadata, f.Bytes!); } }
}
