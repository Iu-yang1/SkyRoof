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
      new(actual,20),new(desired,65));
    Assert.Equal(expected,bounded.Azimuth,5);
    Assert.InRange(Math.Abs(bounded.Azimuth-actual),
      0,Rc3040sPlannerFeedbackGuard.MaximumLeadDegrees);
    Assert.InRange(Math.Abs(bounded.Elevation-20),
      0,Rc3040sPlannerFeedbackGuard.MaximumLeadDegrees);
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
