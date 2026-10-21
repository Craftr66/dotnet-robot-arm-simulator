namespace AxisLab.Core;

/// <summary>Conservative simulation proxies: link capsules, joint spheres, oriented boxes and ground.</summary>
public static class Collision
{
    private record Capsule(V3 A, V3 B, double Radius, int Link);
    private static Capsule[] Body(Device d, RobotState state, RigidTransform basis)
    {
        var g = d.Robot!.Geometry!;
        var diameters = new[] { g.Joint1Diameter, g.Joint2Diameter, g.Joint3Diameter, g.Joint4Diameter, g.Joint5Diameter, g.Joint6Diameter };
        var points = state.JointMatrices.Select(m => V3.Position(basis.Then(new(m)))).ToArray();
        var result = new List<Capsule>();
        for (var i = 0; i < points.Length; i++)
        {
            var end = i + 1 < points.Length ? points[i + 1] : V3.Position(state.FlangeWorld.Matrix);
            result.Add(new(points[i], end, Math.Min(diameters[i], diameters[Math.Min(i + 1, points.Length - 1)]) * .38, i));
            result.Add(new(points[i], points[i], diameters[i] / 2, i));
        }
        result.Add(new(V3.Position(state.FlangeWorld.Matrix), V3.Position(state.TcpWorld.Matrix), 8, points.Length));
        return result.ToArray();
    }
    private static double Distance(V3 p1, V3 q1, V3 p2, V3 q2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        var a = d1.Dot(d1); var e = d2.Dot(d2); var f = d2.Dot(r); double s, t;
        if (a < 1e-12 && e < 1e-12) return r.Length;
        if (a < 1e-12) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            var c = d1.Dot(r);
            if (e < 1e-12) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                var b = d1.Dot(d2); var den = a * e - b * b;
                s = den > 1e-12 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p1 + d1 * s - p2 - d2 * t).Length;
    }
    private static bool HitsBox(Capsule c, RigidTransform box, V3 half, double margin)
    {
        var inverse = box.Inverse(); var a = V3.Transform(inverse, c.A); var b = V3.Transform(inverse, c.B);
        var starts = new[] { a.X, a.Y, a.Z }; var ends = new[] { b.X, b.Y, b.Z };
        var extents = new[] { half.X, half.Y, half.Z }; double low = 0, high = 1;
        for (var i = 0; i < 3; i++)
        {
            var extent = extents[i] + c.Radius + margin; var delta = ends[i] - starts[i];
            if (Math.Abs(delta) < 1e-10) { if (Math.Abs(starts[i]) > extent) return false; }
            else
            {
                var t1 = (-extent - starts[i]) / delta; var t2 = (extent - starts[i]) / delta;
                low = Math.Max(low, Math.Min(t1, t2)); high = Math.Min(high, Math.Max(t1, t2));
                if (low > high) return false;
            }
        }
        return true;
    }
    public static string? Check(ProjectDocument project, RuntimeSnapshot runtime, Guid robotId, double margin = 2)
    {
        var robot = project.Devices.Single(d => d.Id == robotId);
        var state = runtime.Robots.Single(r => r.DeviceId == robotId);
        var basis = new RigidTransform(runtime.Devices.Single(d => d.DeviceId == robotId).World.Matrix);
        var body = Body(robot, state, basis);
        var byId = project.Devices.ToDictionary(d => d.Id);
        bool AttachedTo(Device d, Guid parent)
        {
            while (d.ParentId.HasValue) { if (d.ParentId.Value == parent) return true; d = byId[d.ParentId.Value]; }
            return false;
        }
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (Math.Min(c.A.Z, c.B.Z) - c.Radius < margin) return "机械臂与地面干涉";
            for (var j = i + 1; j < body.Length; j++)
                if (Math.Abs(c.Link - body[j].Link) > 1 && Distance(c.A, c.B, body[j].A, body[j].B) < c.Radius + body[j].Radius + margin)
                    return $"机械臂自身连杆 {c.Link + 1} 与 {body[j].Link + 1} 干涉";
            foreach (var obstacle in project.Devices.Where(d => d.Visible && (d.TypeId is "geometry.box" or "calibration.board" or "calibration.marker") && !AttachedTo(d, robotId)))
            {
                var p = obstacle.Parameters;
                var box = new RigidTransform(runtime.Devices.Single(f => f.DeviceId == obstacle.Id).World.Matrix);
                if (obstacle.TypeId.StartsWith("calibration.")) box = box.Then(RigidTransform.FromPose(new(Z: -p.SizeZ / 2)));
                if (HitsBox(c, box, new(p.SizeX / 2, p.SizeY / 2, p.SizeZ / 2), margin))
                    return $"机械臂与「{obstacle.Name}」干涉";
            }
            foreach (var other in project.Devices.Where(d => d.Robot is not null))
            {
                var otherBasis = new RigidTransform(runtime.Devices.Single(f => f.DeviceId == other.Id).World.Matrix);
                var g = other.Robot!.Geometry!;
                var baseCenter = otherBasis.Then(RigidTransform.FromPose(new(0, 0, g.BaseHeight / 2)));
                if ((other.Id != robotId || c.Link > 0) && HitsBox(c, baseCenter, new(g.Joint1Diameter * 1.05, g.Joint1Diameter * 1.05, g.BaseHeight / 2), 0))
                    return $"机械臂与「{other.Name}」基座干涉";
                if (other.Id == robotId) continue;
                foreach (var part in Body(other, runtime.Robots.Single(r => r.DeviceId == other.Id), otherBasis))
                    if (Distance(c.A, c.B, part.A, part.B) < c.Radius + part.Radius + margin) return $"机械臂与「{other.Name}」干涉";
            }
        }
        return null;
    }
}
