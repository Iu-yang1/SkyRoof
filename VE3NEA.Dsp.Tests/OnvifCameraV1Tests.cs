using System;
using System.Linq;
using Newtonsoft.Json;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class OnvifCameraV1Tests
{
    [Fact]
    public void CameraConfigIsDetachedWhenDialogIsCancelled()
    {
        var source = new OnvifCameraSettings
        {
            CameraName = "Roof",
            DeviceServiceUrl = "http://192.168.1.4/onvif/device_service",
            Username = "camera",
            Password = "test-secret",
            PreferredProfileToken = "main",
            AutoConnect = true,
            AutoPlay = false
        };
        var copy = source.Clone();
        copy.CameraName = "Different";
        copy.Password = "new-secret";
        Assert.Equal("Roof", source.CameraName);
        Assert.Equal("test-secret", source.Password);
        Assert.False(source.AutoPlay);
    }

    [Fact]
    public void OnlySafeHttpDeviceServiceEndpointsAreAccepted()
    {
        Assert.True(new OnvifCameraSettings
        { DeviceServiceUrl = "http://192.168.1.4/onvif/device_service" }.IsConfigured);
        Assert.Equal("", OnvifCameraSettings.ValidateEndpoint(
            "https://example.com/onvif/device_service"));
        Assert.NotEqual("", OnvifCameraSettings.ValidateEndpoint("rtsp://192.168.1.4/live"));
        Assert.NotEqual("", OnvifCameraSettings.ValidateEndpoint(
            "http://admin:secret@192.168.1.4/onvif/device_service"));
    }

    [Fact]
    public void SelectsPreferredMediaVersionAndTokenDeterministically()
    {
        var profiles = new[]
        {
            new OnvifMediaProfile("main", "Media1 main", false),
            new OnvifMediaProfile("main", "Media2 main", true),
            new OnvifMediaProfile("sub", "Media2 sub", true)
        };
        var wanted = OnvifCameraSettings.SelectProfile(profiles, "main", true);
        Assert.True(wanted.Media2);
        Assert.Equal("main", wanted.Token);

        var fallback = OnvifCameraSettings.SelectProfile(profiles, "missing", false);
        Assert.False(fallback.Media2);
        Assert.False(OnvifCameraPanel.ShouldAutoplay(new OnvifCameraSettings()));
        Assert.False(OnvifCameraPanel.ShouldAutoplay(new OnvifCameraSettings
        { DeviceServiceUrl = "http://127.0.0.1/onvif/device_service", AutoPlay = false }));
    }

    [Fact]
    public void PasswordNeverAppearsInPlaintextSettingsJson()
    {
        var config = new OnvifCameraSettings
        {
            DeviceServiceUrl = "http://192.168.1.4/onvif/device_service",
            Username = "operator",
            Password = "camera-password-sentinel"
        };
        var json = JsonConvert.SerializeObject(config);
        Assert.DoesNotContain("camera-password-sentinel", json, StringComparison.Ordinal);
        Assert.Contains("PasswordProtected", json);
        var restored = JsonConvert.DeserializeObject<OnvifCameraSettings>(json);
        Assert.Equal(config.Password, restored!.Password);
    }
}
