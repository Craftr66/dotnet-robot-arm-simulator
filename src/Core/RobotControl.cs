namespace AxisLab.Core;

public sealed partial class ProjectStore
{
    private readonly TimeProvider controlClock;
    private sealed class Control(Guid owner, long timestamp)
    {
        public Guid Owner = owner;
        public long Heartbeat = timestamp;
        public uint Counter;
    }
    private readonly Dictionary<Guid, Control> controls = [];
    private int ControlRemaining(Guid id) => controls.TryGetValue(id, out var c)
        ? Math.Max(0, 5000 - (int)controlClock.GetElapsedTime(c.Heartbeat).TotalMilliseconds) : 0;
    private void ExpireControls()
    {
        foreach (var id in controls.Keys.Where(id => ControlRemaining(id) == 0).ToArray())
        {
            controls.Remove(id);
            if (motions.TryGetValue(id, out var motion) && motion.Status == "Running")
            { motion.Status = "Cancelled"; revision++; dirty = true; }
        }
    }
    public T WithExternalDevice<T>(Guid expectedSession, Guid id, long configurationRevision, Func<Device, T> action)
    {
        lock (gate)
        {
            if (expectedSession != sessionId) throw new DomainException("SessionChanged", "工程会话已变化，请重新选择设备。", 409);
            var device = Find(id);
            if (device.ConfigurationRevision != configurationRevision) throw new DomainException("ConfigurationChanged", "设备配置已变化，请重新读取并确认。", 409);
            return action(device);
        }
    }
    public void ClaimRobot(Guid id, Guid owner)
    {
        lock (gate)
        {
            ExpireControls();
            CheckProgramController(owner);
            if (Find(id).Robot is null) throw new DomainException("NotRobot", "此设备不是机械臂。");
            if (controls.TryGetValue(id, out var c))
            {
                if (c.Owner != owner) throw new DomainException("ControlBusy", "机械臂控制权已被占用。", 409);
                return;
            }
            if (motions[id].Status == "Running") throw new DomainException("RobotBusy", "机械臂正在运动。", 409);
            controls[id] = new(owner, controlClock.GetTimestamp());
        }
    }
    public void HeartbeatOwner(Guid owner, uint counter)
    {
        lock (gate)
        {
            ExpireControls();
            foreach (var c in controls.Values.Where(c => c.Owner == owner && c.Counter != counter))
            { c.Counter = counter; c.Heartbeat = controlClock.GetTimestamp(); }
        }
    }
    public void ReleaseRobot(Guid id, Guid owner)
    {
        lock (gate)
        {
            ExpireControls();
            if (!controls.TryGetValue(id, out var c) || c.Owner != owner) throw new DomainException("NoControl", "当前连接未持有控制权。", 409);
            StopRobot(id);
        }
    }
    public void DisconnectOwner(Guid owner)
    {
        lock (gate) foreach (var id in controls.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) StopRobot(id);
    }
}
