using System;
using System.Collections.Generic;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class Rc3040sMotionPlannerTests
{
  private static Rc3040sMotionPlanner.Limits Limits => new(
    4.0,3.5,0.8,2.0,0,450,0,90);

  [Theory]
  [InlineData(Rc3040sMotionProfile.PrecisionStop)]
  [InlineData(Rc3040sMotionProfile.Trapezoidal)]
  [InlineData(Rc3040sMotionProfile.SCurve)]
  public void OfflineDualAxisTrajectoryAlwaysRespectsSoftLimitsAndSpeedEnvelope(
    Rc3040sMotionProfile mode)
  {
    var p=new Rc3040sMotionPlanner();
    double az=45,el=30;
    for(int i=0;i<1800;i++)
    {
      var s=p.Step(az,el,345,65,0.1,mode,Limits);
      Assert.InRange(s.Azimuth,0,450);
      Assert.InRange(s.Elevation,0,90);
      Assert.InRange(Math.Abs(s.AzSpeed),0,4.000001);
      Assert.InRange(Math.Abs(s.ElSpeed),0,3.500001);
      Assert.InRange(Math.Abs(s.AzAcceleration),0,0.800001);
      Assert.InRange(Math.Abs(s.ElAcceleration),0,0.800001);
      az=s.Azimuth;el=s.Elevation; // ideal actuator simulation only
    }
    Assert.InRange(az,50,345);
    Assert.InRange(el,30,65);
  }

  [Fact]
  public void ScurveReferenceLimitsJerkWhileAccelerating()
  {
    var p=new Rc3040sMotionPlanner();
    double current=0,previousAcceleration=0;
    for(int i=0;i<250;i++)
    {
      var s=p.Step(current,10,200,10,0.1,Rc3040sMotionProfile.SCurve,Limits);
      Assert.InRange(Math.Abs(s.AzAcceleration-previousAcceleration),0,0.200001);
      previousAcceleration=s.AzAcceleration;
      current=s.Azimuth;
    }
  }

  [Fact]
  public void LongAzimuthPositionIsNotWrappedTo360()
  {
    var p=new Rc3040sMotionPlanner();
    var state=p.Step(410,45,445,45,0.5,Rc3040sMotionProfile.Trapezoidal,Limits);
    Assert.InRange(state.Azimuth,410,445);
    Assert.InRange(state.Elevation,44.9,45.1);
    Assert.Equal("W445 045\r",System.Text.Encoding.ASCII.GetString(
      NativeRotatorCodec.Point(RotatorProtocolKind.Rc3040sLan,new(445,45))));
  }

  [Fact]
  public void ResetAndNewTargetReinitializeReferenceAfterStop()
  {
    var p=new Rc3040sMotionPlanner();
    _=p.Step(20,15,250,40,0.4,Rc3040sMotionProfile.Trapezoidal,Limits);
    p.Reset();
    var v=p.Step(100,20,110,30,0.1,Rc3040sMotionProfile.Trapezoidal,Limits);
    Assert.InRange(v.Azimuth,100,100.01);
    Assert.InRange(v.Elevation,20,20.01);
  }

  [Fact]
  public void NeverGenerateUnboundedSetpointWhenFeedbackLags()
  {
    var p=new Rc3040sMotionPlanner();
    for(int i=0;i<2000;i++)
    {
      var s=p.Step(30,20,450,90,0.1,Rc3040sMotionProfile.SCurve,Limits);
      Assert.InRange(s.Azimuth,30,36.00001);
      Assert.InRange(s.Elevation,20,25.25001);
    }
  }

  [Fact]
  public void InvalidInputsAndNaNFailClosed()
  {
    var p=new Rc3040sMotionPlanner();
    Assert.Throws<ArgumentOutOfRangeException>(()=>p.Step(double.NaN,0,10,10,0.1,
      Rc3040sMotionProfile.SCurve,Limits));
    Assert.Throws<ArgumentOutOfRangeException>(()=>p.Step(10,0,451,10,0.1,
      Rc3040sMotionProfile.SCurve,Limits));
    Assert.Throws<ArgumentOutOfRangeException>(()=>p.Step(10,0,30,10,0,
      Rc3040sMotionProfile.SCurve,Limits));
    Assert.False(Rc3040sMotionPlanner.ValidLimits(Limits with{MaxJerk=0}));
  }

  [Fact]
  public void FeedbackEstimatorUsesMechanicalAzimuthAndRejectsStaleSamples()
  {
    var estimate=new Rc3040sFeedbackEstimator();
    DateTime start=new(2026,10,24,12,0,0,DateTimeKind.Utc);
    Assert.False(estimate.Observe(new(359,30),start,out _));
    Assert.True(estimate.Observe(new(361,31),start.AddSeconds(1),out var v));
    Assert.Equal(2,v.AzDegPerSec,5);
    Assert.Equal(1,v.ElDegPerSec,5);
    Assert.False(estimate.Observe(new(362,31),start.AddSeconds(1),out _));
    Assert.False(estimate.Observe(new(365,33),start.AddSeconds(6),out _));
    Assert.False(estimate.Observe(new(445,33),start.AddSeconds(7),out _));
  }

  [Fact]
  public void SettingsDoNotAuthorizePhysicalPlannerByDefault()
  {
    var s=new RotatorSettings{NativeProtocol=RotatorProtocolKind.Rc3040sLan};
    Assert.Equal(Rc3040sMotionProfile.Direct,s.Rc3040sMotionMode);
    Assert.False(s.Rc3040sPlannerEnabled);
    Assert.False(s.Rc3040sMotionHardwareValidated);
  }
}
