using SkyRoof;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class EmbeddedSkyCatSettingsTests
  {
    [Fact]
    public void ExistingCatConfigurationNeverStartsSerialServerByDefault()
    {
      var settings=new CatSettings();
      Assert.False(settings.Embedded.Enabled);
      Assert.False(settings.RxCat.UseEmbeddedSkyCat);
      Assert.False(settings.TxCat.UseEmbeddedSkyCat);
      Assert.Null(settings.Embedded.Validate());
    }

    [Fact]
    public void EmbeddedCatRefusesToShareLoopbackListenerWithExternalServer()
    {
      using var listener=new TcpListener(IPAddress.Loopback,0);
      listener.Start();
      int port=((IPEndPoint)listener.LocalEndpoint).Port;
      Assert.False(EmbeddedSkyCatHost.PortAvailable(port));
      Assert.False(EmbeddedSkyCatHost.PortAvailable(0));
      Assert.False(EmbeddedSkyCatHost.PortAvailable(65536));
    }

    [Fact]
    public void EmbeddedAndExternalCatEndpointsNeverShareAnEngine()
    {
      var external=new CatRadioSettings
      {Enabled=true,Host="localhost",Port=4532};
      var internalCat=new CatRadioSettings
      {Enabled=true,Host="127.0.0.1",Port=4532,UseEmbeddedSkyCat=true};
      Assert.False(CatControl.IsSameHostPort(external,internalCat));
      Assert.True(CatControl.IsSameHostPort(internalCat,new CatRadioSettings
      {Enabled=true,UseEmbeddedSkyCat=true,Host="other-host",Port=4321}));
    }

    [Fact]
    public void EnabledServiceRequiresExplicitSerialPort()
    {
      var settings=new EmbeddedSkyCatSettings {Enabled=true};
      Assert.Contains("serial",settings.Validate()!,System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllEnabledEndpointsMustHaveDistinctPorts()
    {
      var s=new EmbeddedSkyCatSettings {Enabled=true,SerialPort="COM9",CwPort=4532};
      Assert.Contains("unique",s.Validate()!,System.StringComparison.OrdinalIgnoreCase);
      s.CwPort=4538;
      Assert.Null(s.Validate());
      var options=s.BuildOptions();
      Assert.Equal("COM9",options.RigFile);
      Assert.Equal("3081",options.Model);
      Assert.False(options.AllowRemote);
      Assert.Equal(4537,options.SwitchPort);
      Assert.Equal(4538,options.CwPort);
    }
  }
}
