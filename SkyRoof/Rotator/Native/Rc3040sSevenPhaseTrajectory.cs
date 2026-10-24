using System;

namespace SkyRoof.NativeRotator;

/// <summary>
/// Analytic rest-to-rest, symmetric seven-phase jerk-limited S-curve for a
/// fixed endpoint. Phases with zero duration collapse to a 5- or 3-phase
/// trajectory. This is a mathematical reference, NOT a firmware velocity
/// command. For continuously moving satellite targets, the online planner
/// remains responsible for retargeting without assuming physical compliance.
/// </summary>
internal sealed class Rc3040sSevenPhaseTrajectory
{
  internal readonly record struct Sample(double Position,double Velocity,double Acceleration,double Jerk);
  private readonly double start,sign,distance,jerk;
  private readonly double[] durations=new double[7];
  private readonly double[] phaseTimes=new double[8];
  private readonly double[] x=new double[8],v=new double[8],a=new double[8];
  private readonly int[] signJerk={1,0,-1,0,-1,0,1};
  internal double TotalSeconds=>phaseTimes[7];
  internal ReadOnlySpan<double> PhaseDurations=>durations;

  internal Rc3040sSevenPhaseTrajectory(double from,double to,
    double maxSpeed,double maxAccel,double maxJerk)
  {
    if(!double.IsFinite(from)||!double.IsFinite(to)||
       !double.IsFinite(maxSpeed)||!double.IsFinite(maxAccel)||!double.IsFinite(maxJerk)||
       maxSpeed<=0||maxAccel<=0||maxJerk<=0)
      throw new ArgumentOutOfRangeException(nameof(maxSpeed));
    start=from;sign=Math.Sign(to-from);
    distance=Math.Abs(to-from);jerk=maxJerk;
    if(distance==0)return;
    double tj,ta,tv;
    // Fastest symmetric accelerative/decelerative motion reaching vmax.
    if(maxSpeed>=maxAccel*maxAccel/maxJerk)
    {
      tj=maxAccel/maxJerk;
      ta=maxSpeed/maxAccel-tj;
    }
    else
    {
      tj=Math.Sqrt(maxSpeed/maxJerk);
      ta=0;
    }
    double required=2*maxSpeed*(tj+ta/2);
    if(distance>=required)
    {
      tv=(distance-required)/maxSpeed;
    }
    else
    {
      tv=0;
      double taThreshold=2*maxAccel*maxAccel*maxAccel/
        (maxJerk*maxJerk);
      if(distance>=taThreshold && maxSpeed>=maxAccel*maxAccel/maxJerk)
      {
        tj=maxAccel/maxJerk;
        ta=(Math.Sqrt(tj*tj+4*distance/maxAccel)-3*tj)/2;
        ta=Math.Max(0,ta);
      }
      else
      {
        tj=Math.Cbrt(distance/(2*maxJerk));
        ta=0;
      }
    }
    durations[0]=durations[2]=durations[4]=durations[6]=tj;
    durations[1]=durations[5]=ta;durations[3]=tv;
    for(int i=0;i<7;i++)
    {
      double t=durations[i],j=signJerk[i]*jerk;
      phaseTimes[i+1]=phaseTimes[i]+t;
      x[i+1]=x[i]+v[i]*t+a[i]*t*t/2+j*t*t*t/6;
      v[i+1]=v[i]+a[i]*t+j*t*t/2;
      a[i+1]=a[i]+j*t;
    }
  }

  internal Sample At(double seconds)
  {
    if(!double.IsFinite(seconds))throw new ArgumentOutOfRangeException(nameof(seconds));
    if(seconds<=0||distance==0)return new(start,0,0,0);
    if(seconds>=TotalSeconds)return new(start+sign*distance,0,0,0);
    int phase=0;
    while(phase<6 && seconds>=phaseTimes[phase+1])phase++;
    double t=seconds-phaseTimes[phase];
    double j=signJerk[phase]*jerk;
    double pos=x[phase]+v[phase]*t+a[phase]*t*t/2+j*t*t*t/6;
    double vel=v[phase]+a[phase]*t+j*t*t/2;
    double acc=a[phase]+j*t;
    return new(start+sign*Math.Clamp(pos,0,distance),sign*vel,sign*acc,sign*j);
  }
}
