using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxisLab.VisionDemo;

internal static class Program
{
    private static readonly List<string> checks = new List<string>();
    private static void Check(string label, bool condition) { if (!condition) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS: " + label); }
    private static Frame NewFrame() => new Frame { Endpoint = "http://127.0.0.1:5000", ReceivedUtc = DateTime.UtcNow, RobotConfiguration = 2,
        Png = new byte[] { 1, 2, 3 }, Info = new FrameInfo { frameId = "f", cameraId = "c", sessionId = "s", sceneRevision = 10, cameraConfigurationRevision = 3 },
        Robot = new RobotInfo { deviceId = "r", status = "Completed", joints = new double[] { 1, 2, 3 }, flangeBase = new Coordinate { pose = new Pose() }, tcpBase = new Coordinate { pose = new Pose() } } };
    private static VisionResult NewResult() => new VisionResult { FrameId = "f", RequestId = 42, CalibrationOk = true, Found = true, SolutionHash = "hash", FlowSignature = "flow", X = 100, Y = 50, Rz = 30 };
    private static RuntimeInfo NewRuntime() => new RuntimeInfo { sessionId = "s", projectRevision = 10 };
    private static DeviceInfo[] NewDevices() => new[] { new DeviceInfo { id = "c", configurationRevision = 3 }, new DeviceInfo { id = "r", configurationRevision = 2 } };
    private static void Reject(string name, Action<Frame, VisionResult, RuntimeInfo, DeviceInfo[]> mutate)
    {
        var frame = NewFrame(); var result = NewResult(); var runtime = NewRuntime(); var devices = NewDevices(); var binding = CalibrationBinding.Bind("test", frame, result);
        mutate(frame, result, runtime, devices);
        try { binding.Validate(frame, result, runtime, devices, "hash", "flow", frame.Endpoint); throw new Exception("Unexpected authorization: " + name); }
        catch (InvalidOperationException) { Check(name, true); }
    }
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try
        {
            var frame = NewFrame(); var result = NewResult(); var binding = CalibrationBinding.Bind("test", frame, result);
            binding.Validate(frame, result, NewRuntime(), NewDevices(), "hash", "flow", frame.Endpoint); Check("matching current calibrated result passes workflow gate", true);
            Reject("NG detection rejects movement", (f, r, t, d) => r.Found = false);
            Reject("invalid calibration rejects movement", (f, r, t, d) => r.CalibrationOk = false);
            Reject("old frame result cannot be applied to new image", (f, r, t, d) => f.Info.frameId = "other");
            Reject("result without a verified request identity cannot move", (f, r, t, d) => r.RequestId = 0);
            Reject("project restart invalidates calibration binding", (f, r, t, d) => t.sessionId = "new-session");
            Reject("camera configuration change invalidates binding", (f, r, t, d) => d[0].configurationRevision++);
            Reject("robot tool or geometry change invalidates binding", (f, r, t, d) => d[1].configurationRevision++);
            Reject("deleted device cannot be rebound by name", (f, r, t, d) => d[0].id = "replacement");
            Reject("expired frame rejects movement", (f, r, t, d) => f.ReceivedUtc = DateTime.UtcNow.AddSeconds(-31));
            Reject("scene changes after capture reject movement", (f, r, t, d) => t.projectRevision++);
            Reject("changed VM source or output mapping rejects old result", (f, r, t, d) => r.FlowSignature = "changed");
            Reject("changed solution rejects old result", (f, r, t, d) => r.SolutionHash = "changed");
            Reject("non-finite visual coordinates reject movement", (f, r, t, d) => r.X = double.NaN);
            Reject("eye-in-hand cannot use planar fixed-camera shortcut", (f, r, t, d) => f.MovingCamera = true);
            Reject("another endpoint cannot receive a stale result", (f, r, t, d) => f.Endpoint = "http://127.0.0.1:6000");
            var sampleDirectory = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/vision-demo-model-checks"); Directory.CreateDirectory(sampleDirectory);
            frame.Save(sampleDirectory); var saved = File.ReadAllText(Path.Combine(sampleDirectory, "f.observation.json"));
            Check("sample export pairs one image with its robot pose and omits camera truth", saved.Contains("flangeBase") && !saved.Contains("cameraWorld") && !saved.Contains("intrinsics") && File.ReadAllBytes(Path.Combine(sampleDirectory, "f.png")).SequenceEqual(frame.Png));
            frame.Robot.status = "Running";
            try { frame.Save(sampleDirectory); throw new Exception("moving calibration sample accepted"); } catch (InvalidOperationException) { Check("moving calibration sample is rejected", true); }
            var words = new ushort[2]; Modbus.Float(words, 0, -123.5); Check("Modbus Float32 uses high word first", Modbus.U32(words, 0) == BitConverter.ToUInt32(BitConverter.GetBytes(-123.5f), 0));
            try { Modbus.Float(words, 0, double.PositiveInfinity); throw new Exception("infinite transport target accepted"); } catch (InvalidOperationException) { Check("non-finite transport values are rejected", true); }
            if (args.Length > 0)
            {
                using (var axis = new AxisClient(args[0]))
                {
                    var robot = (await axis.Devices(CancellationToken.None)).First(d => d.typeId == "robot.generic3r");
                    var before = (await axis.Runtime(CancellationToken.None)).robots.Single(r => r.deviceId == robot.id).tcpBase.pose;
                    var target = new Pose { x = before.x + 10, y = before.y, z = before.z - 10 };
                    await axis.Request<object>($"v1/robots/{robot.id}/preview", CancellationToken.None, new { target, frame = "base", kind = "MoveJ", speedPercent = 50, positionOnly = true });
                    var validated = 0;
                    await RobotChannel.Move(axis, robot, target, 50, () => { validated++; return Task.CompletedTask; }, CancellationToken.None);
                    var after = (await axis.Runtime(CancellationToken.None)).robots.Single(r => r.deviceId == robot.id);
                    Check("net48 Modbus transport executes a real Cartesian movement", after.status == "Completed" && Math.Abs(after.tcpBase.pose.x - target.x) < .05 && Math.Abs(after.tcpBase.pose.z - target.z) < .05);
                    Check("transport checks result context before acquire and movement", validated == 2);
                    using (var cancel = new CancellationTokenSource())
                    {
                        var destination = new Pose { x = target.x + 40, y = target.y, z = target.z - 40 };
                        var moving = RobotChannel.Move(axis, robot, destination, 1, () => Task.CompletedTask, cancel.Token);
                        var deadline = DateTime.UtcNow.AddSeconds(10); RobotInfo active;
                        do { await Task.Delay(50); active = (await axis.Runtime(CancellationToken.None)).robots.Single(r => r.deviceId == robot.id); } while (active.status != "Running" && DateTime.UtcNow < deadline && !moving.IsCompleted);
                        Check("slow transport movement really starts", active.status == "Running"); cancel.Cancel();
                        try { await moving; } catch (Exception) when (cancel.IsCancellationRequested) { }
                        await Task.Delay(300); var stopped = (await axis.Runtime(CancellationToken.None)).robots.Single(r => r.deviceId == robot.id);
                        await Task.Delay(200); var frozen = (await axis.Runtime(CancellationToken.None)).robots.Single(r => r.deviceId == robot.id);
                        Check("cancellation closes private Modbus socket and freezes actual joints", stopped.status == "Cancelled" && stopped.joints.SequenceEqual(frozen.joints));
                    }
                }
            }
            File.WriteAllText(Path.Combine(sampleDirectory, "model-results.json"), Json.Write(new { checks }));
            Console.WriteLine($"{checks.Count} workflow/transport checks passed."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
