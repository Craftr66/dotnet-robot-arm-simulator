using System.Diagnostics;

namespace AxisLab.Core;

public record IkResult(double[] Joints, double PositionError, double OrientationErrorDegrees, bool NearSingularity, int Iterations);
public static class InverseKinematics
{
    public static double[,] Jacobian(RobotSettings settings,double[] q,bool positionOnly)
    {
        var current=new RigidTransform(RobotModels.Forward(settings,q).Tcp.Matrix); var rows=positionOnly?3:6; var j=new double[rows,q.Length];
        for(var axis=0;axis<q.Length;axis++)
        {
            var probe=q.ToArray();probe[axis]+=.001;
            // Finite differences may cross the limit by epsilon; use a backwards probe at an upper limit.
            var step=probe[axis]>RobotModels.Definitions(settings.ModelId)[axis].Maximum ? -.001 : .001; probe[axis]=q[axis]+step;
            var fk=new RigidTransform(RobotModels.Forward(settings,probe).Tcp.Matrix);var dp=(V3.Position(fk.Values)-V3.Position(current.Values))*(1/step);var dr=MotionMath.RotationError(fk,current)*(100/step);
            var v=new[]{dp.X,dp.Y,dp.Z,dr.X,dr.Y,dr.Z};for(var r=0;r<rows;r++)j[r,axis]=v[r];
        }
        return j;
    }
    public static bool NearSingular(RobotSettings settings,double[] q,bool positionOnly)
    {
        var j=Jacobian(settings,q,positionOnly);var n=j.GetLength(0);var gram=new double[n,n];double largest=0,smallest=double.MaxValue;
        for(var r=0;r<n;r++)for(var c=0;c<n;c++)for(var k=0;k<q.Length;k++)gram[r,c]+=j[r,k]*j[c,k];
        for(var k=0;k<n;k++)
        {
            var pivot=k;for(var r=k+1;r<n;r++)if(Math.Abs(gram[r,k])>Math.Abs(gram[pivot,k]))pivot=r;
            var v=Math.Abs(gram[pivot,k]);largest=Math.Max(largest,v);smallest=Math.Min(smallest,v);if(v<1e-10)return true;
            for(var c=k;c<n;c++)(gram[k,c],gram[pivot,c])=(gram[pivot,c],gram[k,c]);
            for(var r=k+1;r<n;r++){var f=gram[r,k]/gram[k,k];for(var c=k;c<n;c++)gram[r,c]-=f*gram[k,c];}
        }
        return smallest/Math.Max(largest,1e-20)<1e-5;
    }
    public static IkResult Solve(RobotSettings settings,RigidTransform target,double[] seed,bool positionOnly,bool multipleSeeds=true)
    {
        RobotModels.ValidateJoints(settings.ModelId,seed);
        if(seed.Length==3&&!positionOnly)throw new DomainException("UnsupportedPose","三轴仅支持位置目标，不能独立控制完整姿态。");
        var g=settings.Geometry!;var reach=g.BaseHeight+g.ShoulderHeight+g.UpperArm+g.Forearm+g.Wrist1+g.Wrist2+g.FlangeLength+new V3(settings.ToolTransform.X,settings.ToolTransform.Y,settings.ToolTransform.Z).Length;
        if(V3.Position(target.Values).Length>reach+1)throw new DomainException("IkUnreachable","目标超出机械臂几何可达范围。");
        var clock=Stopwatch.StartNew();var definitions=RobotModels.Definitions(settings.ModelId);var random=new Random(7351); IkResult? best=null;
        for(var attempt=0;attempt<(multipleSeeds?12:1);attempt++)
        {
            var q=attempt==0?seed.ToArray():attempt==1?definitions.Select(d=>Math.Clamp(0,d.Minimum,d.Maximum)).ToArray():definitions.Select((d,i)=>Math.Clamp(seed[i]+(random.NextDouble()-.5)*240,d.Minimum,d.Maximum)).ToArray();
            var damping=.05;
            for(var iteration=0;iteration<180;iteration++)
            {
                if(clock.Elapsed.TotalMilliseconds>450)break;
                var actual=new RigidTransform(RobotModels.Forward(settings,q).Tcp.Matrix);var dp=V3.Position(target.Values)-V3.Position(actual.Values);var dr=MotionMath.RotationError(target,actual);
                if(dp.Length<.015&&(positionOnly||dr.Length<.0002))
                {
                    var solution=new IkResult(q,dp.Length,dr.Length*180/Math.PI,NearSingular(settings,q,positionOnly),iteration);
                    if(best is null||q.Zip(seed).Sum(x=>Math.Pow(x.First-x.Second,2))<best.Joints.Zip(seed).Sum(x=>Math.Pow(x.First-x.Second,2)))best=solution;
                    break;
                }
                var error=positionOnly?new[]{dp.X,dp.Y,dp.Z}:new[]{dp.X,dp.Y,dp.Z,100*dr.X,100*dr.Y,100*dr.Z};
                var j=Jacobian(settings,q,positionOnly);var n=q.Length;var a=new double[n,n];var b=new double[n];
                for(var r=0;r<n;r++){for(var c=0;c<n;c++)for(var k=0;k<error.Length;k++)a[r,c]+=j[k,r]*j[k,c];a[r,r]+=damping*damping;for(var k=0;k<error.Length;k++)b[r]+=j[k,r]*error[k];}
                var delta=MotionMath.Solve(a,b);if(delta is null)break;
                var scale=Math.Min(1,6/Math.Max(1e-10,delta.Max(Math.Abs)));var baseline=error.Sum(v=>v*v);var improved=false;
                for(var search=0;search<7;search++)
                {
                    var next=q.Select((v,i)=>Math.Clamp(v+delta[i]*scale,definitions[i].Minimum,definitions[i].Maximum)).ToArray();
                    var result=new RigidTransform(RobotModels.Forward(settings,next).Tcp.Matrix);var p=V3.Position(target.Values)-V3.Position(result.Values);var r=MotionMath.RotationError(target,result);
                    if(p.Dot(p)+(positionOnly?0:10000*r.Dot(r))<baseline){q=next;improved=true;damping=Math.Max(.01,damping*.7);break;}scale*=.5;
                }
                if(!improved){damping*=4;if(damping>100)break;}
            }
            if(best is not null && (attempt==0||clock.Elapsed.TotalMilliseconds>150))return best;
            if(clock.Elapsed.TotalMilliseconds>450)break;
        }
        return best??throw new DomainException("IkUnreachable","未找到满足限位和误差要求的 IK 解；目标可能不可达或接近奇异，请更换姿态/种子。");
    }
}
