using System;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class Rc3040sPlannerFeedbackGuardTests
{
  private static readonly DateTime Started =
    new(2026,10,28,7,0,0,DateTimeKind.Utc);
  private static readonly RotatorPosition StartingPoint=new(30,20);
  private static readonly RotatorPosition FinalPoint=new(120,65);
  private static readonly Rc3040sMotionPlanner.Limits WireLimits=
    new(2,2,0.8,1.5,0,450,0,180);

  [Fact]
  public void SmallQuantizedWStillRequiresTwoPositionConfirmations()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    var actual=new RotatorPosition(30.4,20);
    var endpoint=new RotatorPosition(31,20);
    guard.TryConsumeFreshFeedback(Started,actual,FinalPoint,out _);
    guard.MarkTransmitted(Started,actual,endpoint);
    Assert.True(guard.HasInFlightSetpoint); // 0.6° may still move
    guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(500),endpoint,FinalPoint,out _);
    Assert.True(guard.HasInFlightSetpoint);
    guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(850),endpoint,FinalPoint,out _);
    Assert.False(guard.HasInFlightSetpoint);
  }

  [Fact]
  public void NewRequestedTargetMatchingC2DoesNotClearOutstandingMotion()
  {
    // A moving motor was ordered to W032 021, while target tracking
    // changes back to the still-reported C2=30/20. The old code could
    // clear requiresStopAfterReconnect on two samples at 30/20 even
    // though W032 021 had not been completed.
    Assert.False(Rc3040sPlannerFeedbackGuard.IsLastWireAtReportedPosition(
      new(30,20),new RotatorPosition(32,21),true));
    Assert.False(Rc3040sPlannerFeedbackGuard.IsLastWireAtReportedPosition(
      new(30,20),null,true));
    Assert.True(Rc3040sPlannerFeedbackGuard.IsLastWireAtReportedPosition(
      new(32,21),new RotatorPosition(32,21),true));
    Assert.True(Rc3040sPlannerFeedbackGuard.IsLastWireAtReportedPosition(
      new(30,20),new RotatorPosition(32,21),false));
  }

  [Fact]
  public void PlannerCannotAdvanceMoreThanOncePerC2Feedback()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    Assert.True(guard.TryConsumeFreshFeedback(Started,StartingPoint,
      FinalPoint,out bool stalled));
    Assert.False(stalled);
    for(int i=0;i<20;i++)
      Assert.False(guard.TryConsumeFreshFeedback(Started,StartingPoint,
        FinalPoint,out _));
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(500),StartingPoint,FinalPoint,out _));
    Assert.False(guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(100),StartingPoint,FinalPoint,out _));
  }

  [Theory]
  [InlineData(30,120,31)]
  [InlineData(30,0,29)]
  [InlineData(30.4,120,31)]
  [InlineData(30.8,120,32)]
  [InlineData(30.8,0,30)]
  public void RoundedWEndpointRemainsWithinMeasuredLead(
    double actual,double desired,double expected)
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    var bounded=guard.BoundSetpoint(
      new(actual,20),new(desired,65),WireLimits);
    Assert.Equal(expected,bounded.Azimuth,5);
    Assert.InRange(Math.Abs(bounded.Azimuth-actual),
      0,Rc3040sPlannerFeedbackGuard.MaximumLeadDegrees);
    Assert.InRange(Math.Abs(bounded.Elevation-20),
      0,Rc3040sPlannerFeedbackGuard.MaximumLeadDegrees);
  }

  [Theory]
  [InlineData(60.6,60.1,60.6,60)]
  [InlineData(60.0,59.7,60.0,60)]
  [InlineData(35.4,35.6,35.4,36)]
  public void FractionalSoftwareLimitNeverRoundsWireTargetOutsideLimit(
    double bound,double actual,double desired,double expected)
  {
    var limits=bound>50 ?
      WireLimits with { MaxAz=bound } :
      WireLimits with { MinAz=bound };
    var guard=new Rc3040sPlannerFeedbackGuard();
    Assert.True(Rc3040sPlannerFeedbackGuard.ValidQuantizedLimits(limits));
    var command=guard.BoundSetpoint(
      new(actual,20),new(desired,21),limits);
    Assert.Equal(expected,command.Azimuth);
    Assert.InRange(command.Azimuth,limits.MinAz,limits.MaxAz);
    Assert.InRange(Math.Abs(command.Azimuth-actual),
      0,Rc3040sPlannerFeedbackGuard.MaximumLeadDegrees);
  }

  [Fact]
  public void RejectsWireLimitsWithNoRepresentableIntegerAngle()
  {
    var invalid=WireLimits with { MinAz=30.1,MaxAz=30.9 };
    Assert.False(Rc3040sPlannerFeedbackGuard.ValidQuantizedLimits(invalid));
    Assert.Throws<ArgumentOutOfRangeException>(()=>
      new Rc3040sPlannerFeedbackGuard().BoundSetpoint(
        new(30.5,20),new(30.6,21),invalid));
  }

  [Theory]
  [InlineData(-10,360)]
  [InlineData(0,451)]
  public void RejectsLimitsOutsideRc3040sWireAzRange(
    double min,double max)
  {
    Assert.False(Rc3040sPlannerFeedbackGuard.ValidQuantizedLimits(
      WireLimits with { MinAz=min,MaxAz=max }));
  }

  [Fact]
  public void NoC2PositionProgressAfterRepeatedSetpointsRequestsStop()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    Assert.True(guard.TryConsumeFreshFeedback(Started,
      StartingPoint,FinalPoint,out _));
    guard.MarkTransmitted(Started,StartingPoint,new(31,21));
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(3),StartingPoint,FinalPoint,out bool before));
    Assert.False(before);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(4200),StartingPoint,FinalPoint,out bool after));
    Assert.True(after);
  }

  [Fact]
  public void ActualProgressExtendsWatchdogButNeverUsesReferenceAsFeedback()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    guard.TryConsumeFreshFeedback(Started,StartingPoint,FinalPoint,out _);
    guard.MarkTransmitted(Started,StartingPoint,new(32,20));
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(3),new(31,20),FinalPoint,out bool stall));
    Assert.False(stall);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(6),new(31,20),FinalPoint,out stall));
    Assert.False(stall);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(8),new(31,20),FinalPoint,out stall));
    Assert.True(stall);
    guard.Reset();
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(10),new(31,20),FinalPoint,out stall));
    Assert.False(stall);
  }

  [Fact]
  public void FreshC2BeforePreviousWaypointSettlesNeverAuthorizesNextW()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    Assert.True(guard.TryConsumeFreshFeedback(
      Started,StartingPoint,FinalPoint,out _));
    guard.MarkTransmitted(Started,StartingPoint,new(32,21));
    Assert.True(guard.HasInFlightSetpoint);
    for(int tick=1;tick<=8;tick++)
    {
      // Stay at least 0.5 degree outside the previous W endpoint:
      // otherwise the firmware's arrived-at-setpoint condition applies.
      var moving=new RotatorPosition(30+tick*0.17,20+tick*0.08);
      Assert.True(guard.TryConsumeFreshFeedback(
        Started.AddMilliseconds(tick*300),moving,FinalPoint,out bool stalled));
      Assert.False(stalled);
      Assert.True(guard.HasInFlightSetpoint);
      Assert.Throws<InvalidOperationException>(()=>
        guard.MarkTransmitted(Started.AddMilliseconds(tick*300),
          moving,new(33,22)));
    }
    // One C2 at the waypoint is insufficient, even if target equals.
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(3),new(32,21),FinalPoint,out _));
    Assert.True(guard.HasInFlightSetpoint);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddMilliseconds(3400),new(32,21),FinalPoint,out _));
    Assert.False(guard.HasInFlightSetpoint);
    guard.MarkTransmitted(Started.AddMilliseconds(3500),
      new(32,21),new(33,22));
    Assert.True(guard.HasInFlightSetpoint);
  }

  [Fact]
  public void STOPReleasesPendingSetpointWithoutSendingAnotherW()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    guard.TryConsumeFreshFeedback(Started,StartingPoint,FinalPoint,out _);
    guard.MarkTransmitted(Started,StartingPoint,new(31,21));
    Assert.True(guard.HasInFlightSetpoint);
    guard.Reset();
    Assert.False(guard.HasInFlightSetpoint);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(1),StartingPoint,FinalPoint,out _));
  }

  [Fact]
  public void NearFinalC2MustNotClearUnconfirmedMotorMove()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    var final=new RotatorPosition(31,20);
    guard.TryConsumeFreshFeedback(Started,StartingPoint,final,out _);
    guard.MarkTransmitted(Started,StartingPoint,final);
    // A controller might still report 30 while the user target is 31;
    // 1.5-degree terminal tolerance cannot fake W completion.
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(2),StartingPoint,final,out bool stalled));
    Assert.False(stalled);
    Assert.True(guard.HasInFlightSetpoint);
    Assert.True(guard.TryConsumeFreshFeedback(
      Started.AddSeconds(5),StartingPoint,final,out stalled));
    Assert.True(stalled);
  }

  [Fact]
  public void ReachingFinalTargetDoesNotTripNoProgressFault()
  {
    var guard=new Rc3040sPlannerFeedbackGuard();
    var nearbyGoal=new RotatorPosition(31,20);
    guard.TryConsumeFreshFeedback(Started,StartingPoint,nearbyGoal,out _);
    guard.MarkTransmitted(Started,StartingPoint,nearbyGoal);
    guard.TryConsumeFreshFeedback(
      Started.AddSeconds(1),nearbyGoal,nearbyGoal,out bool stalled);
    Assert.False(stalled);
    guard.TryConsumeFreshFeedback(
      Started.AddSeconds(20),nearbyGoal,nearbyGoal,out stalled);
    Assert.False(stalled);
  }
}
