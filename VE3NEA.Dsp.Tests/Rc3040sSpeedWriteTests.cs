using System;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class Rc3040sSpeedWriteTests
{
  [Theory]
  [InlineData(true,5000,4.977876106)]
  [InlineData(false,5000,5.514705882)]
  public void NominalDegreesPerSecondConversionMatchesFirmwareConstants(
    bool azimuth,int raw,double expected)
  {
    double speed=Rc3040sManagementService.SpeedUnits.ToDegreesPerSecond(azimuth,raw);
    Assert.InRange(speed,expected-0.000001,expected+0.000001);
    Assert.InRange(Rc3040sManagementService.SpeedUnits.ToRaw(azimuth,speed),raw-1,raw+1);
  }

  [Theory]
  [InlineData(true,5000)]
  [InlineData(false,5000)]
  [InlineData(true,10000)]
  [InlineData(false,10000)]
  public void SliderDisplaysSpeedAndMapsBackWithinOneHundredthOfASecond(bool azimuth,int raw)
  {
    int tick=Rc3040sManagementService.SpeedUnits.SliderTick(azimuth,raw);
    int applied=Rc3040sManagementService.SpeedUnits.RawFromSlider(azimuth,tick,15,10000);
    double error=Math.Abs(
      Rc3040sManagementService.SpeedUnits.ToDegreesPerSecond(azimuth,applied)-
      Rc3040sManagementService.SpeedUnits.ToDegreesPerSecond(azimuth,raw));
    Assert.InRange(error,0,0.0051);
    Assert.InRange(applied,15,10000);
  }

  [Theory]
  [InlineData(5000,5500,5500,false,0)]
  [InlineData(5000,5500,5000,false,1)]
  [InlineData(5000,5500,5000,true,3)]
  [InlineData(5000,5500,null,true,2)]
  [InlineData(5000,5500,5200,false,4)]
  public void DistinguishesChangedReadbackFromEchoAndNoEffect(int before,int desired,
    int? readback,bool echo,int expected)
  {
    Assert.Equal((Rc3040sManagementService.SpeedWriteOutcome)expected,
      Rc3040sManagementService.ClassifySpeedWrite(before,desired,readback,echo));
  }

  [Fact]
  public void NumericGuardRejectsInvalidSpeed()
  {
    Assert.Throws<ArgumentOutOfRangeException>(()=>
      Rc3040sManagementService.SpeedUnits.ToRaw(true,double.NaN));
    Assert.Throws<ArgumentOutOfRangeException>(()=>
      Rc3040sManagementService.SpeedUnits.ToRaw(false,0));
  }

  [Fact]
  public void DemoFieldsNoLongerExistInPublicSettings()
  {
    var names=typeof(SkyRoof.RotatorSettings).GetProperties();
    foreach(var prop in names)
      Assert.DoesNotContain("Demo",prop.Name,StringComparison.OrdinalIgnoreCase);
  }
}
