namespace AxisLab.Core;

public readonly record struct V3(double X, double Y, double Z)
{
    public static V3 operator +(V3 a, V3 b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static V3 operator *(V3 a, double b) => new(a.X*b,a.Y*b,a.Z*b);
    public double Dot(V3 b) => X*b.X+Y*b.Y+Z*b.Z;
    public double Length => Math.Sqrt(Dot(this));
    public static V3 Position(double[] m) => new(m[12],m[13],m[14]);
    public static V3 Position(RigidTransform m) => Position(m.Values);
    public static V3 Transform(RigidTransform m, V3 p) => Transform(m.Values, p);
    public static V3 Transform(double[] m, V3 p) => new(m[0]*p.X+m[4]*p.Y+m[8]*p.Z+m[12], m[1]*p.X+m[5]*p.Y+m[9]*p.Z+m[13],m[2]*p.X+m[6]*p.Y+m[10]*p.Z+m[14]);
}
public readonly record struct Rotation(double X, double Y, double Z, double W)
{
    public static Rotation From(double[] m)
    {
        double x,y,z,w;
        var trace=m[0]+m[5]+m[10];
        if(trace>0) { var s=Math.Sqrt(trace+1)*2; w=s/4; x=(m[6]-m[9])/s; y=(m[8]-m[2])/s; z=(m[1]-m[4])/s; }
        else if(m[0]>m[5] && m[0]>m[10]) { var s=Math.Sqrt(1+m[0]-m[5]-m[10])*2; w=(m[6]-m[9])/s; x=s/4; y=(m[4]+m[1])/s; z=(m[8]+m[2])/s; }
        else if(m[5]>m[10]) { var s=Math.Sqrt(1+m[5]-m[0]-m[10])*2; w=(m[8]-m[2])/s; x=(m[4]+m[1])/s; y=s/4; z=(m[9]+m[6])/s; }
        else { var s=Math.Sqrt(1+m[10]-m[0]-m[5])*2; w=(m[1]-m[4])/s; x=(m[8]+m[2])/s; y=(m[9]+m[6])/s; z=s/4; }
        var n=Math.Sqrt(x*x+y*y+z*z+w*w); return new(x/n,y/n,z/n,w/n);
    }
    public static Rotation Slerp(Rotation a, Rotation b, double u)
    {
        var dot=a.X*b.X+a.Y*b.Y+a.Z*b.Z+a.W*b.W;
        if(dot<0) { b=new(-b.X,-b.Y,-b.Z,-b.W); dot=-dot; }
        double x,y;
        if(dot>.9995) { x=1-u;y=u; }
        else { var angle=Math.Acos(Math.Clamp(dot,-1,1)); x=Math.Sin((1-u)*angle)/Math.Sin(angle); y=Math.Sin(u*angle)/Math.Sin(angle); }
        var q=new Rotation(x*a.X+y*b.X,x*a.Y+y*b.Y,x*a.Z+y*b.Z,x*a.W+y*b.W);
        var n=Math.Sqrt(q.X*q.X+q.Y*q.Y+q.Z*q.Z+q.W*q.W); return new(q.X/n,q.Y/n,q.Z/n,q.W/n);
    }
    public RigidTransform At(V3 p)
    {
        var x=X;var y=Y;var z=Z;var w=W;
        return new([1-2*(y*y+z*z),2*(x*y+z*w),2*(x*z-y*w),0,2*(x*y-z*w),1-2*(x*x+z*z),2*(y*z+x*w),0,2*(x*z+y*w),2*(y*z-x*w),1-2*(x*x+y*y),0,p.X,p.Y,p.Z,1]);
    }
}
public static class MotionMath
{
    public static void ValidatePose(Pose p, string code="InvalidPose")
    {
        if(p is null || new[]{p.X,p.Y,p.Z,p.Rx,p.Ry,p.Rz}.Any(v=>!double.IsFinite(v)||Math.Abs(v)>1_000_000)) throw new DomainException(code,"位姿须为有限数值且绝对值不超过 1000000。");
    }
    public static RigidTransform Interpolate(RigidTransform a,RigidTransform b,double u) => Rotation.Slerp(Rotation.From(a.Values),Rotation.From(b.Values),u).At(V3.Position(a.Values)*(1-u)+V3.Position(b.Values)*u);
    public static V3 RotationError(RigidTransform target,RigidTransform actual)
    {
        var q=Rotation.From(target.Then(actual.Inverse()).Values);
        if(q.W<0) q=new(-q.X,-q.Y,-q.Z,-q.W);
        var v=new V3(q.X,q.Y,q.Z); var n=v.Length;
        return n<1e-12 ? v*2 : v*(2*Math.Atan2(n,Math.Clamp(q.W,-1,1))/n);
    }
    public static double Smooth(double u) => u*u*u*(10+u*(-15+6*u));
    public static double[]? Solve(double[,] a,double[] b)
    {
        var n=b.Length; var m=new double[n,n+1];
        for(var i=0;i<n;i++){ for(var j=0;j<n;j++)m[i,j]=a[i,j];m[i,n]=b[i]; }
        for(var k=0;k<n;k++)
        {
            var pivot=k;for(var i=k+1;i<n;i++)if(Math.Abs(m[i,k])>Math.Abs(m[pivot,k]))pivot=i;
            if(Math.Abs(m[pivot,k])<1e-14)return null;
            for(var j=k;j<=n;j++)(m[k,j],m[pivot,j])=(m[pivot,j],m[k,j]);
            var divisor=m[k,k];for(var j=k;j<=n;j++)m[k,j]/=divisor;
            for(var i=0;i<n;i++)if(i!=k){var f=m[i,k];for(var j=k;j<=n;j++)m[i,j]-=f*m[k,j];}
        }
        return Enumerable.Range(0,n).Select(i=>m[i,n]).ToArray();
    }
}
