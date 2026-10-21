using AxisLab.Core;
using System.Text.Json;

static class MotionChecks
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool okay, string name) { if (!okay) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); count++; }
        void Reject(Action action, string code) { try { action(); throw new Exception("Expected "+code); } catch (DomainException e) { Check(e.Code == code, code+" (motion)"); } }
        double Error(double[] a, double[] b) => (V3.Position(a)-V3.Position(b)).Length;
        var random = new Random(5601);
        foreach (var type in new[] { "robot.generic6r", "robot.generic3r" })
        {
            var settings = RobotModels.Default(type); bool okay = true;
            for (var trial = 0; trial < 40; trial++)
            {
                var q = settings.JointPositions.Select((_,i)=> (random.NextDouble()-.5)*(i==0?200:100)).ToArray();
                var wanted = new RigidTransform(RobotModels.Forward(settings, q).Tcp.Matrix);
                var seed = q.Select(v=>v+(random.NextDouble()-.5)*8).ToArray();
                var ik = InverseKinematics.Solve(settings,wanted,seed,type.EndsWith("3r"));
                var actual = new RigidTransform(RobotModels.Forward(settings,ik.Joints).Tcp.Matrix);
                okay &= Error(wanted.Values,actual.Values)<.02 && (type.EndsWith("3r") || MotionMath.RotationError(wanted,actual).Length<.0003);
            }
            Check(okay, type+" 40 seeded IK/FK round trips");
        }
        var generic = Generic6R.Default();
        Reject(()=>InverseKinematics.Solve(generic,RigidTransform.FromPose(new(100000,0,0)),new double[6],false),"IkUnreachable");
        Reject(()=>InverseKinematics.Solve(RobotModels.Default("robot.generic3r"),RigidTransform.Identity,new double[3],false),"UnsupportedPose");
        var a = RigidTransform.FromPose(new(10,20,30,15,90,170)); var b = a.Then(a.Inverse());
        Check(b.Values.Zip(RigidTransform.Identity.Values).All(p=>Math.Abs(p.First-p.Second)<1e-8),"rigid inverse at Euler singularity");
        var r1 = RigidTransform.FromPose(new(Rz:179)); var r2 = RigidTransform.FromPose(new(Rz:-179));
        Check(Math.Abs(MotionMath.RotationError(MotionMath.Interpolate(r1,r2,.5),r1).Length-Math.PI/180)<1e-8,"SLERP uses shortest rotation across 180 degrees");
        var store = new ProjectStore(Path.Combine(root,"motion5")); var ws = store.Add("robot.generic6r",store.Get().Revision); var id = ws.Project.Devices[0].Id;
        store.MoveJoints(id,[15,-15,20,10,25,-5],100,ws.Revision); store.AdvanceMotions(20);
        var s = store.GetRuntime().Robots[0]; Check(s.Status=="Completed","joint path reaches regular nonsingular start");
        var start = new RigidTransform(s.TcpBase.Matrix); var p = s.TcpBase.Pose;
        var target = p with { X=p.X-25,Y=p.Y+15,Z=p.Z+10 };
        var preview = store.PreviewCartesian(id,new(target,Kind:"MoveL",SpeedPercent:50));
        Check(preview.Duration>0 && preview.Samples>=2,"MoveL preview generates IK path without motion");
        var revision = store.Get().Revision; var poseId = store.GetRuntime().Robots[0].PoseId;
        Reject(()=>store.MoveCartesian(id,new(new(90000,0,0)),revision),"IkUnreachable");
        Check(store.Get().Revision==revision && store.GetRuntime().Robots[0].PoseId==poseId,"rejected IK does not mutate scene or teleport robot");
        store.MoveCartesian(id,new(target,Kind:"MoveL",SpeedPercent:50),revision);
        var vector = new V3(target.X-start.Values[12],target.Y-start.Values[13],target.Z-start.Values[14]); var length2 = vector.Dot(vector);
        bool line=true, orientation=true, speedOkay=true, accelOkay=true;
        var lastQ = s.Joints; var lastV = new double[6]; int samples=0;
        while(store.HasActiveMotion)
        {
            store.AdvanceMotions(.01); var rt = store.GetRuntime().Robots[0]; var v = V3.Position(rt.TcpBase.Matrix)-V3.Position(start.Values);
            var along = Math.Clamp(v.Dot(vector)/length2,0,1); line &= (v-vector*along).Length<.065;
            orientation &= MotionMath.RotationError(new(rt.TcpBase.Matrix),start).Length<.001;
            var velocity = rt.Joints.Zip(lastQ).Select(p=>(p.First-p.Second)/.01).ToArray();
            for(int j=0;j<6;j++) { speedOkay &= Math.Abs(velocity[j])<=Generic6R.Definitions[j].MaxVelocity*.501; if(samples>0) accelOkay &= Math.Abs((velocity[j]-lastV[j])/.01)<=Generic6R.Definitions[j].MaxVelocity*1.02; }
            lastQ=rt.Joints;lastV=velocity;samples++;
            if(samples>20000) throw new Exception("MoveL failed to finish");
        }
        Check(line && orientation,"MoveL runtime samples stay within 0.065 mm and 0.058 deg of line");
        Check(speedOkay && accelOkay,"sampled joint velocity and acceleration remain within configured caps");
        Check(Error(store.GetRuntime().Robots[0].TcpBase.Matrix,RigidTransform.FromPose(target).Values)<.02,"MoveL endpoint agrees with Cartesian target");
        // Five points are physical poses; playback is planned in full before any motion starts.
        for(int i=0;i<5;i++)
        {
            var q = store.GetRuntime().Robots[0].Joints; q[0]+=2;
            store.MoveJoints(id,q,100,store.Get().Revision); store.AdvanceMotions(20);
            store.RecordTeachPoint(id,"P"+(i+1),"work",store.Get().Revision);
        }
        var saved=store.Save("五点示教",store.Get().Revision);
        Check(saved.Teaching!.Length==5 && saved.Teaching.All(p=>!p.Stale),"five valid teach points recorded");
        var opened=new ProjectStore(Path.Combine(root,"motion5")); opened.Open(saved.Project.Id.ToString("D"),opened.Get().Revision);
        var points=opened.Get().Project.TeachPoints!;
        Check(points.Length==5 && points.Select(p=>p.Id).SequenceEqual(saved.Project.TeachPoints!.Select(p=>p.Id)),"teach identities and coordinates survive disk reopen");
        var playback=opened.PlayTeachPoints(id,points.Select(p=>p.Id).ToArray(),"MoveJ",100,opened.Get().Revision);
        Check(playback.Runtime.Robots[0].StepCount==5,"playback exposes five execution steps");
        opened.AdvanceMotions(.1); var stop=opened.StopRobot(id); var stopped=stop.Runtime.Robots[0].Joints; opened.AdvanceMotions(30);
        Check(opened.GetRuntime().Robots[0].Joints.SequenceEqual(stopped),"teach playback stop preserves intermediate pose");
        opened.PlayTeachPoints(id,points.Select(p=>p.Id).ToArray(),"MoveJ",100,opened.Get().Revision); opened.AdvanceMotions(100);
        Check(opened.GetRuntime().Robots[0].Status=="Completed" && opened.GetRuntime().Robots[0].Joints.SequenceEqual(points[^1].Joints),"five-point playback completes at final saved joints");
        var device=opened.Get().Project.Devices[0]; opened.Update(id,device with {Name="重命名"},opened.Get().Revision);
        Check(opened.Get().Teaching!.All(p=>!p.Stale),"rename does not invalidate teach geometry dependencies");
        device=opened.Get().Project.Devices[0]; opened.Update(id,device with {Robot=device.Robot! with {ToolTransform=new(0,0,85)}},opened.Get().Revision);
        Check(opened.Get().Teaching!.All(p=>p.Stale),"TCP change marks teach points stale");
        Reject(()=>opened.PlayTeachPoints(id,[points[0].Id],"MoveJ",25,opened.Get().Revision),"StaleTeachPoint");
        opened.EditTeachPoint(points[0].Id,"P1","revalidate",opened.Get().Revision);
        Check(!opened.Get().Teaching![0].Stale && opened.Get().Teaching![1].Stale,"explicit revalidation updates only selected point");
        device=opened.Get().Project.Devices[0]; opened.Update(id,device with {Robot=device.Robot! with {WorkTransform=new(100,20,30,Rz:25)}},opened.Get().Revision);
        Check(opened.Get().Teaching![0].Stale,"work frame change invalidates revalidated point");
        var work=opened.Get().Project.Devices[0].Robot!.WorkTransform!;
        var current=opened.GetRuntime().Robots[0].TcpBase;
        var workPose=RigidTransform.FromPose(work).Inverse().Then(new(current.Matrix)).Frame().Pose;
        Check(opened.PreviewCartesian(id,new(workPose,"work")).Joints.Length==6,"work frame target resolves to robot base");
        var obstacle=store.Add("geometry.box",store.Get().Revision).Project.Devices.Last();
        store.Update(obstacle.Id,obstacle with {Transform=new(400,0,545),Parameters=obstacle.Parameters with {SizeX=150,SizeY=150,SizeZ=150}},store.Get().Revision);
        Reject(()=>store.MoveJoints(id,new double[6],100,store.Get().Revision),"CollisionDetected");
        var crash = new ProjectStore(Path.Combine(root,"collision5")); var crashId=crash.Add("robot.generic6r",crash.Get().Revision).Project.Devices[0].Id;
        Reject(()=>crash.MoveJoints(crashId,[0,90,0,0,0,0],100,crash.Get().Revision),"CollisionDetected");
        Check(crash.GetRuntime().Robots[0].Joints.All(q=>q==0),"ground or self collision blocks path before start");
        var mid = new ProjectStore(Path.Combine(root,"midpath5")); var midId=mid.Add("robot.generic6r",mid.Get().Revision).Project.Devices[0].Id;
        var block=mid.Add("geometry.box",mid.Get().Revision).Project.Devices.Last();
        mid.Update(block.Id,block with {Transform=new(350,350,545),Parameters=block.Parameters with {SizeX=50,SizeY=50,SizeZ=50}},mid.Get().Revision);
        Check(Collision.Check(mid.Get().Project,mid.GetRuntime(),midId) is null,"obstacle permits the start pose");
        Reject(()=>mid.MoveJoints(midId,[90,0,0,0,0,0],100,mid.Get().Revision),"CollisionDetected");
        Check(mid.GetRuntime().Robots[0].Status=="Idle","midpath collision is rejected before motion starts");
        var together=new ProjectStore(Path.Combine(root,"simultaneous5")); var first=together.Add("robot.generic6r",together.Get().Revision).Project.Devices[0];
        together.Update(first.Id,first with {Transform=new(0,-400,0)},together.Get().Revision);
        var second=together.Add("robot.generic6r",together.Get().Revision).Project.Devices.Last();
        together.Update(second.Id,second with {Transform=new(0,400,0)},together.Get().Revision);
        together.MoveJoints(first.Id,[90,0,0,0,0,0],100,together.Get().Revision); together.MoveJoints(second.Id,[-90,0,0,0,0,0],100,together.Get().Revision);
        together.AdvanceMotions(10);var intersected=together.GetRuntime().Robots;
        Check(intersected.All(r=>r.Status=="Failed" && r.MotionErrorCode=="CollisionDetected"),"simultaneous robots stop when runtime paths intersect");
        Check(Math.Abs(intersected[0].Joints[0])<90 && Math.Abs(intersected[1].Joints[0])<90,"runtime collision retains last safe intermediate joints");
        var three=new ProjectStore(Path.Combine(root,"three5")); var threeId=three.Add("robot.generic3r",three.Get().Revision).Project.Devices[0].Id;
        var threeTarget=RobotModels.Forward(RobotModels.Default("robot.generic3r"),[15,-10,20]).Tcp.Pose;
        three.MoveCartesian(threeId,new(threeTarget,PositionOnly:true),three.Get().Revision);three.AdvanceMotions(100);
        Check(Error(three.GetRuntime().Robots[0].TcpBase.Matrix,RigidTransform.FromPose(threeTarget).Values)<.02,"three-axis Cartesian position command completes");
        File.WriteAllText(Path.Combine(root,"motion5-summary.json"),JsonSerializer.Serialize(new {count,samples,preview},Catalog.Json));
        return count;
    }
}
