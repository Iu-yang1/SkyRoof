using System;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class Rc3040sMotionSimulationTests
{
  private static Rc3040sMotionPlanner.Limits Limits =>
    new(3.0,2.5,0.75,1.5,0,450,0,90);

  [Theory]
  [InlineData(Rc3040sMotionProfile.PrecisionStop)]
  [InlineData(Rc3040sMotionProfile.Trapezoidal)]
  [InlineData(Rc3040sMotionProfile.SCurve)]
  public void TenThousandTickSimulationWithChangingTargetsHasNoNaNOrOutOfBounds(
    Rc3040sMotionProfile mode)
  {
    var planner=new Rc3040sMotionPlanner();
    // Synthetic plant: RC-3040S motor velocity and acceleration are NOT
    // established by this test; the fake plant saturates its own response.
    double actualAz=125,actualEl=20;
    double commandAz=actualAz,commandEl=actualEl;
    var rng=new Random(20261024);
    for(int t=0;t<10000;t++)
    {
      double finalAz=t%2000<1000?330:35;
      double finalEl=t%1300<650?75:10;
      double dt=t%37==0?0.26:t%47==0?0.05:0.10;
      // Inject occasional 250ms command delivery pauses and target refreshes.
      if(t%100!=0)
      {
        var refState=planner.Step(actualAz,actualEl,finalAz,finalEl,dt,mode,Limits);
        commandAz=refState.Azimuth;commandEl=refState.Elevation;
      }
      double maxTravelAz=4.0*dt,maxTravelEl=3.0*dt;
      actualAz+=Math.Clamp(commandAz-actualAz,-maxTravelAz,maxTravelAz);
      actualEl+=Math.Clamp(commandEl-actualEl,-maxTravelEl,maxTravelEl);
      Assert.True(double.IsFinite(actualAz) && double.IsFinite(actualEl));
      Assert.InRange(actualAz,0,450);
      Assert.InRange(actualEl,0,90);
      Assert.InRange(commandAz,0,450);
      Assert.InRange(commandEl,0,90);
      if(t%1000==0 && rng.NextDouble()>0.5)
        planner.Reset(); // STOP/reconnect/ownership handoff resets reference
    }
  }

  [Fact]
  public void DiagnosticsParseObservedYuntaiFramesAndDoNotInventAcknowledgement()
  {
    string raw=
      "RC_3040S_67S_8H_260223_C 01020304050607\r\n"+
      "===========NET SETUP===========\r\nAT+IP=192.168.001.009\r\n"+
      "==========YUNTAI SETUP===========\r\n"+
      "AT+YT=3040\r\nAT+P450\r\n"+
      "AT+AZ_SPD=05000\r\nAT+EL_SPD=05000\r\n";
    Assert.True(Rc3040sManagementService.TryParse(raw,3,false,out var snap));
    Assert.Equal("RC_3040S_67S_8H_260223_C",snap.Firmware);
    Assert.Equal("0607",snap.DeviceIdSuffix);
    Assert.Equal(5000,snap.AzRaw);
    Assert.Equal(5000,snap.ElRaw);
    Assert.Equal("P450",snap.Travel);
    Assert.False(snap.EchoVerified);
    Assert.True(Rc3040sManagementService.TryParse(raw,3,true,out var echoed));
    Assert.True(echoed.EchoVerified);
  }

  [Fact]
  public void InvalidSpeedFramesAreReadOnlyAndNotPromotedToWritableValues()
  {
    string prefix="RC_3040S_67S_8H_260223_C 01020304050607\r\nNET SETUP\r\n";
    Assert.True(Rc3040sManagementService.TryParse(prefix+
      "AT+AZ_SPD=99999\r\nAT+EL_SPD=00000\r\n",1,false,out var snap));
    Assert.Null(snap.AzRaw);
    Assert.Null(snap.ElRaw);
  }

  [Fact]
  public void LongOrUnidentifiedManagementFramesFailClosed()
  {
    Assert.False(Rc3040sManagementService.TryParse(new string('A',17000),1,false,out _));
    Assert.False(Rc3040sManagementService.TryParse("NET SETUP\r\nAT+AZ_SPD=05000\r\n",
      1,false,out _));
    Assert.False(Rc3040sManagementService.TryParse("RC_3040S_67S_8H_260223_C NET SETUP",
      41,false,out _));
  }
}
