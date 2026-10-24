using System;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

/// <summary>
/// NRL2 UDP support was moved to DTracNRL2.NET. Existing old SkyRoof JSON
/// must never be reinterpreted as RC-3040S LAN motor-control settings.
/// </summary>
public sealed class Rc3040sLanMigrationTests
{
  [Fact]
  public void OldNumericProtocol10NeverOpensAController()
  {
    var settings=JsonConvert.DeserializeObject<RotatorSettings>(
      "{\"NativeProtocol\":10,\"NativeTransport\":2,\"Enabled\":true,\"Host\":\"203.0.113.25\",\"Port\":60050}")!;
    settings.MigrateDriverMode();
    Assert.False(settings.Enabled);
    Assert.False(NativeRotatorCodec.Supports(settings.NativeProtocol,settings.NativeTransport));
    Assert.False(NativeRotatorCodec.CanQuery(settings.NativeProtocol));
    Assert.False(NativeRotatorCodec.CanJog(settings.NativeProtocol));
    Assert.Null(NativeRotatorCodec.Stop(settings.NativeProtocol));
    Assert.Equal((ushort)60050,settings.Port);
    Assert.Equal("203.0.113.25",settings.Host);
  }

  [Fact]
  public void NewLanProtocolHasDedicatedIdAndNoUdpTransport()
  {
    Assert.Equal(11,(int)RotatorProtocolKind.Rc3040sLan);
    var settings=new RotatorSettings{NativeProtocol=RotatorProtocolKind.Rc3040sLan};
    Assert.Equal(RotatorTransportKind.Tcp,settings.NativeTransport);
    Assert.Equal((ushort)60020,settings.Port);
    Assert.True(NativeRotatorCodec.Supports(settings.NativeProtocol,RotatorTransportKind.Tcp));
    Assert.False(NativeRotatorCodec.Supports(settings.NativeProtocol,RotatorTransportKind.Udp));
    var values=new RotatorProtocolSelectionConverter()
      .GetStandardValues(null)!.Cast<RotatorProtocolKind>().ToArray();
    Assert.Contains(RotatorProtocolKind.Rc3040sLan,values);
    Assert.DoesNotContain(values,p=>(int)p==10);
  }

  [Fact]
  public void LegacyRelayFieldsNoLongerBelongToActiveSkyRoofSettings()
  {
    var names=typeof(RotatorSettings).GetProperties()
      .Select(p=>p.Name).ToArray();
    Assert.DoesNotContain(names,n=>n.StartsWith("Dtrac",StringComparison.OrdinalIgnoreCase));
    var settings=new RotatorSettings{NativeProtocol=RotatorProtocolKind.Rc3040sLan};
    var visible=new RotatorSettingsPropertyConverter()
      .GetProperties(null,settings,new Attribute[]{BrowsableAttribute.Yes})!
      .Cast<PropertyDescriptor>().Select(p=>p.Name).ToArray();
    Assert.Contains(nameof(RotatorSettings.Host),visible);
    Assert.Contains(nameof(RotatorSettings.Port),visible);
    Assert.Contains(nameof(RotatorSettings.Rc3040sMotionMode),visible);
  }
}
