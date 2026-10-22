using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Validation and Kaydet shared by the device editor and the add-device form (Settings, first run).</summary>
public class DeviceFormTests
{
    static DeviceFormInput Input(string host = "192.168.1.2", string http = "80", string rtsp = "554", string sdk = "8000",
        string password = "", string key = "", string name = " NVR ") =>
        new(name, host, http, rtsp, sdk, false, " admin ", password, key);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HostIsRequired(string host)
    {
        Assert.Null(DeviceForm.Read(new Device(), Input(host: host), null, out var error));
        Assert.Equal(DeviceForm.HostRequired, error);
    }

    [Theory]
    [InlineData("0", "554", "8000")]
    [InlineData("80", "65536", "8000")]
    [InlineData("80", "554", "abc")]
    [InlineData("-1", "554", "8000")]
    public void PortsMustBeValid(string http, string rtsp, string sdk)
    {
        Assert.Null(DeviceForm.Read(new Device(), Input(http: http, rtsp: rtsp, sdk: sdk), null, out var error));
        Assert.Equal(DeviceForm.PortsInvalid, error);
    }

    [Fact]
    public void ReadTrimsAndKeepsStoredSecretsWhenBoxesAreEmpty()
    {
        var saved = new Device { Password = "stored", StreamKey = "KEY" };
        var form = DeviceForm.Read(saved, Input(host: " 10.0.0.9 "), null, out var error)!;
        Assert.Null(error);
        Assert.Equal(saved.Id, form.Id);
        Assert.Equal("NVR", form.Name);
        Assert.Equal("10.0.0.9", form.Host);
        Assert.Equal("admin", form.UserName);
        Assert.Equal("stored", form.Password);
        Assert.Equal("KEY", form.StreamKey);

        var typed = DeviceForm.Read(saved, Input(password: "new", key: "K2"), null, out _)!;
        Assert.Equal("new", typed.Password);
        Assert.Equal("K2", typed.StreamKey);
    }

    [Fact]
    public void ApplyToNewDeviceTakesEverything()
    {
        var target = new Device();
        var form = DeviceForm.Read(target, Input(name: "", password: "pw", key: "KEY"), null, out _)!;
        var cameras = new List<Camera> { new() { Channel = 1, Name = "Kapı" }, new() { Channel = 2, Name = "Bahçe", Hidden = true } };
        var details = new DeviceDetails("NVR", "DS-8632NI-I8", "SN", "V4", "NVR");
        Assert.True(DeviceForm.Apply(target, form, isNew: true, pinChanged: false, pin: null, details, cameras));
        Assert.Equal("192.168.1.2", target.Name); // empty name → host
        Assert.Equal("pw", target.Password);
        Assert.Equal("KEY", target.StreamKey);
        Assert.Equal("DS-8632NI-I8", target.Model);
        Assert.Equal("NVR", target.DeviceType);
        Assert.Equal([1, 2], target.Cameras.Select(c => c.Channel));
        Assert.True(target.Cameras[1].Hidden);
    }

    [Fact]
    public void ApplyKeepsCameraObjectsAndReportsLoginChanges()
    {
        var camera = new Camera { Channel = 1, Name = "Eski" };
        var target = new Device { Host = "192.168.1.2", HttpPort = 80, Password = "pw", Cameras = [camera] };
        var drafts = new List<Camera> { DeviceForm.Copy(camera) };
        drafts[0].Name = "Yeni";

        var same = DeviceForm.Read(target, Input(), null, out _)!;
        Assert.False(DeviceForm.Apply(target, same, false, false, null, null, drafts));
        Assert.Same(camera, target.Cameras[0]);
        Assert.Equal("Yeni", camera.Name);

        var moved = DeviceForm.Read(target, Input(http: "8080"), null, out _)!;
        Assert.True(DeviceForm.Apply(target, moved, false, false, null, null, drafts));
        Assert.Equal(8080, target.HttpPort);
    }
}
