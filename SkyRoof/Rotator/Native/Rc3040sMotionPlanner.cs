using System;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Stateful, bounded dual-axis REFERENCE trajectory planner. The RC-3040S
/// GS-232B W command expresses only an integer-degree endpoint, NOT velocity.
/// A generated reference is not proof that the physical shaft obeys this
/// acceleration/jerk constraint. Only used when hardware validation is opted in.
/// </summary>
internal sealed class Rc3040sMotionPlanner
{
  private Axis az = new(), el = new();
  private bool started;
  internal readonly record struct Limits(double AzMaxSpeed,double ElMaxSpeed,
    double MaxAcceleration,double MaxJerk,double MinAz,double MaxAz,double MinEl,double MaxEl);

  internal readonly record struct State(
    double Azimuth,double Elevation,double AzSpeed,double ElSpeed,
    double AzAcceleration,double ElAcceleration);

  private sealed class Axis
  {
    public double Position,Velocity,Acceleration;
    public void Reset(double actual) {Position=actual;Velocity=Acceleration=0;}
  }

  internal static bool ValidLimits(Limits v) =>
    double.IsFinite(v.AzMaxSpeed) && v.AzMaxSpeed is >=0.1 and <=20 &&
    double.IsFinite(v.ElMaxSpeed) && v.ElMaxSpeed is >=0.1 and <=20 &&
    double.IsFinite(v.MaxAcceleration) && v.MaxAcceleration is >=0.05 and <=8 &&
    double.IsFinite(v.MaxJerk) && v.MaxJerk is >=0.05 and <=30 &&
    double.IsFinite(v.MinAz) && double.IsFinite(v.MaxAz) &&
    double.IsFinite(v.MinEl) && double.IsFinite(v.MaxEl) &&
    v.MinAz<=v.MaxAz && v.MinEl<=v.MaxEl;

  internal void Reset() { started=false; az=new(); el=new(); }

  internal State Step(
    double actualAz,double actualEl,double targetAz,double targetEl,double dt,
    Rc3040sMotionProfile profile,Limits limits)
  {
    if(!ValidLimits(limits) || !double.IsFinite(dt) || dt<=0 ||
       !double.IsFinite(actualAz) || !double.IsFinite(actualEl) ||
       !double.IsFinite(targetAz) || !double.IsFinite(targetEl) ||
       actualAz<limits.MinAz || actualAz>limits.MaxAz ||
       actualEl<limits.MinEl || actualEl>limits.MaxEl ||
       targetAz<limits.MinAz || targetAz>limits.MaxAz ||
       targetEl<limits.MinEl || targetEl>limits.MaxEl)
      throw new ArgumentOutOfRangeException(nameof(limits),"Invalid position, time or motor bounds");
    dt=Math.Clamp(dt,0.02,0.5);
    if(!started)
    {
      az.Reset(actualAz);el.Reset(actualEl);started=true;
    }
    if(profile==Rc3040sMotionProfile.Direct)
    {
      az.Reset(targetAz);el.Reset(targetEl);
      return Output();
    }
    Advance(az,actualAz,targetAz,dt,limits.AzMaxSpeed,
      limits.MaxAcceleration,limits.MaxJerk,profile,limits.MinAz,limits.MaxAz);
    Advance(el,actualEl,targetEl,dt,limits.ElMaxSpeed,
      limits.MaxAcceleration,limits.MaxJerk,profile,limits.MinEl,limits.MaxEl);
    return Output();
  }

  private State Output() => new(az.Position,el.Position,az.Velocity,el.Velocity,
    az.Acceleration,el.Acceleration);

  private static void Advance(Axis axis,double actual,double goal,double dt,
    double maxSpeed,double maxAccel,double maxJerk,Rc3040sMotionProfile profile,
    double min,double max)
  {
    // Reinitialize on an unexpectedly large divergence. The engine separately
    // handles feedback jumps, STOP and fault latching; this avoids extrapolating
    // a stale reference across a reconnect or an operator reposition.
    double maxLookAhead=Math.Clamp(maxSpeed*1.5,1.0,6.0);
    if(Math.Abs(axis.Position-actual)>maxLookAhead+2.0)
      axis.Reset(actual);
    double remaining=goal-axis.Position;
    if(Math.Abs(remaining)<0.03 && Math.Abs(axis.Velocity)<0.1)
    {
      axis.Position=goal;axis.Velocity=axis.Acceleration=0;return;
    }

    // Braking envelope: reference v <= sqrt(2*a*d). PrecisionStop uses a
    // lower cap near the target; Trapezoidal has bounded acceleration;
    // S-Curve also bounds the rate of acceleration change (jerk).
    double margin=profile==Rc3040sMotionProfile.PrecisionStop?0.75:0.15;
    double permitted=Math.Min(maxSpeed,Math.Sqrt(
      Math.Max(0.0,2.0*maxAccel*Math.Max(0.0,Math.Abs(remaining)-margin))));
    double desiredVelocity=Math.Sign(remaining)*permitted;
    double wantedAccel=Math.Clamp((desiredVelocity-axis.Velocity)/dt,-maxAccel,maxAccel);
    if(profile==Rc3040sMotionProfile.SCurve)
      axis.Acceleration=Math.Clamp(wantedAccel,
        axis.Acceleration-maxJerk*dt,axis.Acceleration+maxJerk*dt);
    else axis.Acceleration=wantedAccel;

    double nextVelocity=Math.Clamp(axis.Velocity+axis.Acceleration*dt,-maxSpeed,maxSpeed);
    // Prevent stepping farther than either the final goal or the bounded
    // ahead-of-feedback reference window; never cross the target.
    double proposed=axis.Position+nextVelocity*dt;
    if(Math.Sign(remaining)!=Math.Sign(goal-proposed) && remaining!=0)
    {
      proposed=goal;nextVelocity=axis.Acceleration=0;
    }
    double low=Math.Max(min,actual-maxLookAhead);
    double high=Math.Min(max,actual+maxLookAhead);
    proposed=Math.Clamp(proposed,low,high);
    if(Math.Abs(proposed-axis.Position)<1e-9)
      nextVelocity=0;
    axis.Position=proposed;
    axis.Velocity=nextVelocity;
  }
}
