using System;
using System.Linq;
using Newtonsoft.Json;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RotatorDriverSelectionTests
{
  [Fact]
  public void DefaultLocalGs232AutomaticallySelectsNativeDriver()
  {
    var settings=new RotatorSettings();
    Assert.Equal(RotatorDriverMode.Auto,settings.DriverMode);
    Assert.True(settings.ResolveUseNativeDriver());
    Assert.Equal(RotatorTransportKind.Serial,settings.NativeTransport);
    Assert.Equal(RotatorProtocolKind.Gs232,settings.NativeProtocol);
    Assert.Equal("COM9",settings.NativeSerialPort);
  }

  [Fact]
  public void LegacyCustomServerIsPreservedUntilUserSelectsNative()
  {
    var settings=new RotatorSettings{Host="192.168.1.55",Port=4533};
    Assert.False(settings.ResolveUseNativeDriver());

    settings.DriverMode=RotatorDriverMode.Native;
    Assert.True(settings.ResolveUseNativeDriver());
    settings.DriverMode=RotatorDriverMode.LegacyRotctld;
    Assert.False(settings.ResolveUseNativeDriver());
  }

  [Fact]
  public void ExistingNativeTcpAndOldNativeBoolMigrateAutomatically()
  {
    var settings=new RotatorSettings
    {
      Host="10.0.0.20",
      NativeTransport=RotatorTransportKind.Tcp
    };
    Assert.True(settings.ResolveUseNativeDriver());
    settings.NativeTransport=RotatorTransportKind.Serial;
    settings.UseNativeDriver=true;
    Assert.True(settings.ResolveUseNativeDriver());
  }

  [Fact]
  public void OldSavedJsonDefaultFalseMigratesToAutoWithoutUiSteps()
  {
    const string old=@"{""UseNativeDriver"":false,""Host"":""127.0.0.1"",""Port"":4533,""Enabled"":true}";
    var settings=JsonConvert.DeserializeObject<RotatorSettings>(old)!;
    Assert.Equal(RotatorDriverMode.Auto,settings.DriverMode);
    Assert.True(settings.ResolveUseNativeDriver());
    Assert.True(settings.Enabled);
  }

  [Fact]
  public void SettingsPropertiesHaveNoEagerSystemIOPortsAssemblyTypes()
  {
    var types=typeof(RotatorSettings).GetProperties()
      .Select(p=>p.PropertyType.FullName??"").ToArray();
    Assert.DoesNotContain(types,t=>t.StartsWith("System.IO.Ports.",StringComparison.Ordinal));
  }
}
