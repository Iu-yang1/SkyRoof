using System;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Estimate observed shaft angular velocity from two or more fresh C2 replies.
/// Signed mechanical AZ is NOT modulo 360: a 450-degree rotator may traverse
/// continuously beyond 360. This measures position/time, not step frequency.
/// </summary>
internal sealed class Rc3040sFeedbackEstimator
{
  private RotatorPosition? previous;
  private DateTime previousUtc;
  private double az,el;
  private bool haveRate;
  internal readonly record struct Observation(double AzDegPerSec,double ElDegPerSec,
    DateTime TimestampUtc);
  internal void Reset(){previous=null;previousUtc=default;az=el=0;haveRate=false;}
  internal bool Observe(RotatorPosition pos,DateTime utc,out Observation estimate)
  {
    estimate=default;
    if(!NativeRotatorCodec.Valid(pos)||utc.Kind!=DateTimeKind.Utc)return false;
    if(previous is null)
    {
      previous=pos;previousUtc=utc;return false;
    }
    double dt=(utc-previousUtc).TotalSeconds;
    if(dt<=0)return false;
    if(dt>3)
    {
      Reset();previous=pos;previousUtc=utc;return false;
    }
    double dvAz=pos.Azimuth-previous.Value.Azimuth;
    double dvEl=pos.Elevation-previous.Value.Elevation;
    previous=pos;previousUtc=utc;
    // Reject implausible physical jumps before using an estimated rate in
    // any safety or motion display; the engine's position-jump supervisor
    // independently latches its own STOP safety state.
    if(Math.Abs(dvAz)>30 || Math.Abs(dvEl)>30)
    {
      haveRate=false;return false;
    }
    double nowAz=dvAz/dt,nowEl=dvEl/dt;
    if(!double.IsFinite(nowAz)||!double.IsFinite(nowEl)||
       Math.Abs(nowAz)>40||Math.Abs(nowEl)>40)return false;
    if(haveRate)
    {
      az=0.4*nowAz+0.6*az;
      el=0.4*nowEl+0.6*el;
    }
    else {az=nowAz;el=nowEl;haveRate=true;}
    estimate=new Observation(az,el,utc);return true;
  }
}
