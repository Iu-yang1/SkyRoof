using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RotatorSettingsPropertyGridTests
{
  private readonly RotatorSettingsPropertyConverter converter = new();
  private static readonly Attribute[] Browsable = { BrowsableAttribute.Yes };

  private HashSet<string> Visible(RotatorSettings rotator) =>
    converter.GetProperties(null, rotator, Browsable)
      .Cast<PropertyDescriptor>()
      .Select(p => p.Name)
      .ToHashSet(StringComparer.Ordinal);

  [Fact]
  public void DriverChoicesExposeOnlyNativeAndLegacyWithoutAuto()
  {
    var driver = new RotatorDriverModeConverter();
    Assert.Equal(
      new[] { RotatorDriverMode.Native, RotatorDriverMode.LegacyRotctld },
      driver.GetStandardValues(null)!.Cast<RotatorDriverMode>().ToArray());
    Assert.Equal(RotatorDriverMode.Native, new RotatorSettings().DriverMode);
  }

  [Fact]
  public void SerialNativeShowsSerialParametersButNotNetworkOrLegacy()
  {
    var r = new RotatorSettings
    {
      DriverMode = RotatorDriverMode.Native,
      NativeTransport = RotatorTransportKind.Serial,
      NativeProtocol = RotatorProtocolKind.Gs232
    };
    var visible = Visible(r);
    foreach (var name in new[] { "DriverMode", "NativeTransport", "NativeProtocol",
      "NativeSerialPort", "NativeBaudRate", "NativeDataBits", "NativeParity",
      "NativeStopBits", "NativeHandshake", "NativeDtrEnable", "NativeRtsEnable",
      "NativePollMs", "NativeReadTimeout", "Enabled", "MinAzimuth", "ParkAzimuth" })
      Assert.Contains(name, visible);

    foreach (var name in new[] { "Host", "Port", "Delay", "LogTraffic",
      "ReceiveTimeout", "NativePointTemplate", "NativeStopTemplate",
      "NativeQueryTemplate", "NativeResponseTemplate", "UseNativeDriver" })
      Assert.DoesNotContain(name, visible);
  }

  [Theory]
  [InlineData(RotatorTransportKind.Tcp)]
  [InlineData(RotatorTransportKind.Udp)]
  public void NativeNetworkTransportShowsEndpointAndHidesSerialFields(RotatorTransportKind transport)
  {
    var r = new RotatorSettings
    {
      DriverMode = RotatorDriverMode.Native,
      NativeTransport = transport
    };
    var visible = Visible(r);
    Assert.Contains("Host", visible);
    Assert.Contains("Port", visible);
    Assert.Contains("NativeTransport", visible);
    Assert.Contains("NativeProtocol", visible);
    Assert.DoesNotContain("NativeSerialPort", visible);
    Assert.DoesNotContain("NativeBaudRate", visible);
    Assert.DoesNotContain("NativeParity", visible);
    Assert.DoesNotContain("NativeRtsEnable", visible);
    Assert.DoesNotContain("Delay", visible);
  }

  [Fact]
  public void LegacyRotctldShowsHostAndPortWithoutNativeFields()
  {
    var r = new RotatorSettings { DriverMode = RotatorDriverMode.LegacyRotctld };
    var visible = Visible(r);
    foreach (var name in new[] { "DriverMode", "Host", "Port", "Delay",
      "LogTraffic", "SendTimeout", "ReceiveTimeout", "ReconnectDelay", "Enabled" })
      Assert.Contains(name, visible);
    foreach (var name in new[] { "NativeProtocol", "NativeTransport", "NativePollMs",
      "NativeReadTimeout", "NativeSerialPort", "NativeBaudRate", "NativePointTemplate" })
      Assert.DoesNotContain(name, visible);
  }

  [Fact]
  public void TemplateCommandsAppearOnlyForCustomProtocol()
  {
    var r = new RotatorSettings { NativeProtocol = RotatorProtocolKind.Gs232 };
    Assert.DoesNotContain("NativePointTemplate", Visible(r));
    r.NativeProtocol = RotatorProtocolKind.CustomTemplate;
    var visible = Visible(r);
    foreach (var name in new[] { "NativePointTemplate", "NativeStopTemplate",
      "NativeQueryTemplate", "NativeResponseTemplate" })
      Assert.Contains(name, visible);
  }

  [Fact]
  public void SwitchingTransportsRetainsHiddenValues()
  {
    var r = new RotatorSettings
    {
      NativeSerialPort = "COM17",
      Host = "192.168.5.20",
      Port = 8999,
      NativeTransport = RotatorTransportKind.Tcp
    };
    Assert.Contains("Host", Visible(r));
    r.NativeTransport = RotatorTransportKind.Serial;
    Assert.Contains("NativeSerialPort", Visible(r));
    Assert.Equal("COM17", r.NativeSerialPort);
    Assert.Equal("192.168.5.20", r.Host);
    Assert.Equal((ushort)8999, r.Port);
  }

  [Fact]
  public void LegacyAutoMigratesUsingOldRemoteHostHeuristic()
  {
    var remote = new RotatorSettings {
      DriverMode = RotatorDriverMode.Auto,
      Host = "192.168.1.2"
    };
    remote.MigrateDriverMode();
    Assert.Equal(RotatorDriverMode.LegacyRotctld, remote.DriverMode);
    Assert.False(remote.ResolveUseNativeDriver());

    var local = new RotatorSettings {
      DriverMode = RotatorDriverMode.Auto
    };
    local.MigrateDriverMode();
    Assert.Equal(RotatorDriverMode.Native, local.DriverMode);
    Assert.True(local.ResolveUseNativeDriver());
  }
}
