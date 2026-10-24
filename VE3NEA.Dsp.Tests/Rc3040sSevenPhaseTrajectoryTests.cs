using System;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class Rc3040sSevenPhaseTrajectoryTests
{
  [Theory]
  [InlineData(0,180,4.0,0.8,1.5)]
  [InlineData(350,375,3.0,0.8,2.0)]
  [InlineData(70,68,2.0,0.4,1.5)]
  [InlineData(30,30.01,2.0,0.8,2.0)]
  public void AnalyticRestToRestCurveRespectsSpeedAccelerationJerkAndBounds(
    double start,double target,double vmax,double amax,double jmax)
  {
    var traj=new Rc3040sSevenPhaseTrajectory(start,target,vmax,amax,jmax);
    Assert.Equal(7,traj.PhaseDurations.Length);
    Assert.True(traj.TotalSeconds>0);
    double min=Math.Min(start,target),max=Math.Max(start,target);
    const int steps=3000;
    for(int i=0;i<=steps;i++)
    {
      var s=traj.At(i*traj.TotalSeconds/steps);
      Assert.InRange(s.Position,min-1e-6,max+1e-6);
      Assert.InRange(Math.Abs(s.Velocity),0,vmax+1e-6);
      Assert.InRange(Math.Abs(s.Acceleration),0,amax+1e-6);
      Assert.InRange(Math.Abs(s.Jerk),0,jmax+1e-6);
    }
    Assert.Equal(start,traj.At(0).Position,5);
    Assert.Equal(target,traj.At(traj.TotalSeconds).Position,5);
    Assert.Equal(0,traj.At(traj.TotalSeconds).Velocity,5);
  }

  [Fact]
  public void ZeroDistanceAndInvalidParamsAreHandledSafely()
  {
    var zero=new Rc3040sSevenPhaseTrajectory(135,135,3,0.5,1);
    Assert.Equal(0,zero.TotalSeconds);
    Assert.Equal(135,zero.At(100).Position);
    Assert.Throws<ArgumentOutOfRangeException>(()=>
      new Rc3040sSevenPhaseTrajectory(0,10,0,1,1));
    Assert.Throws<ArgumentOutOfRangeException>(()=>
      new Rc3040sSevenPhaseTrajectory(0,10,1,0,double.NaN));
  }

  [Fact]
  public void LongCruiseHasNonZeroConstantVelocityPhase()
  {
    var t=new Rc3040sSevenPhaseTrajectory(0,300,4,0.8,2);
    Assert.True(t.PhaseDurations[3]>0);
    var m=t.At(t.TotalSeconds/2);
    Assert.InRange(m.Velocity,3.999,4.001);
    Assert.InRange(Math.Abs(m.Acceleration),0,1e-5);
  }
}
