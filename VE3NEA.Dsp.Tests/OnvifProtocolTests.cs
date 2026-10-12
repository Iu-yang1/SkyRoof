using System;
using System.Linq;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class OnvifProtocolTests
{
    [Fact]
    public void Media2GetStreamUriUsesProtocolAndProfileTokenWithoutMedia1StreamSetup()
    {
        var request = OnvifProtocolClient.BuildStreamUriRequest(
            new OnvifMediaProfile("profile-20", "HD", true));

        Assert.Equal("http://www.onvif.org/ver20/media/wsdl", request.Name.NamespaceName);
        Assert.Equal("GetStreamUri", request.Name.LocalName);
        Assert.Equal("RTSP", request.Elements().Single(x => x.Name.LocalName == "Protocol").Value);
        Assert.Equal("profile-20", request.Elements().Single(x => x.Name.LocalName == "ProfileToken").Value);
        Assert.DoesNotContain(request.Descendants(), x => x.Name.LocalName == "StreamSetup");
    }

    [Fact]
    public void Media1GetStreamUriUsesOnvifStreamSetupRtpUnicastRtsp()
    {
        var request = OnvifProtocolClient.BuildStreamUriRequest(
            new OnvifMediaProfile("profile-10", "SD", false));

        Assert.Equal("http://www.onvif.org/ver10/media/wsdl", request.Name.NamespaceName);
        Assert.Equal("GetStreamUri", request.Name.LocalName);
        Assert.Equal("profile-10", request.Elements().Single(x => x.Name.LocalName == "ProfileToken").Value);
        var setup = request.Elements().Single(x => x.Name.LocalName == "StreamSetup");
        Assert.Equal("RTP-Unicast", setup.Elements().Single(x => x.Name.LocalName == "Stream").Value);
        Assert.Equal("RTSP", setup.Descendants().Single(x => x.Name.LocalName == "Protocol").Value);
        Assert.DoesNotContain(request.Elements(), x => x.Name.LocalName == "Protocol");
    }

    [Fact]
    public void CameraHardwareLaunchEnforcesDirectD3D11VaAndNeverExposesRtspSecrets()
    {
        string[] args = OnvifGpuPlayer.BuildHardwareOnlyArguments(
            123456, "skyroof-onvif-test");
        Assert.Contains("--no-config", args);
        Assert.Contains("--hwdec=d3d11va", args);
        Assert.Contains("--hwdec-software-fallback=no", args);
        Assert.Contains("--gpu-context=d3d11", args);
        Assert.Contains("--d3d11-warp=no", args);
        Assert.Contains("--rtsp-transport=tcp", args);
        Assert.DoesNotContain(args, x => x.Contains("rtsp://", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, x => x.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, x => x.Contains("hwdec=auto", StringComparison.OrdinalIgnoreCase));
    }
}
