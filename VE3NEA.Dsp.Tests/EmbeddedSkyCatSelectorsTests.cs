using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class EmbeddedSkyCatSelectorsTests
  {
    [Fact]
    public void ModelDropdownUsesInstalledRigIdsInsteadOfHardcodedOptions()
    {
      string directory = Path.Combine(Path.GetTempPath(),
        "skyroof-skycat-model-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      try
      {
        File.WriteAllText(Path.Combine(directory, "IC-9700.json"),
          "{\"id\":3081,\"default_baud_rate\":115200}");
        File.WriteAllText(Path.Combine(directory, "FT-991A.json"),
          "{\"id\":1035}");
        File.WriteAllText(Path.Combine(directory, "invalid.json"), "{broken");
        File.WriteAllText(Path.Combine(directory, "no-id.json"), "{\"echo\":true}");

        var models = EmbeddedSkyCatModelConverter.LoadModels(directory);
        Assert.Equal("IC-9700", models["3081"]);
        Assert.Equal("FT-991A", models["1035"]);
        Assert.Equal(2, models.Count);
      }
      finally
      {
        Directory.Delete(directory, recursive: true);
      }
    }

    [Fact]
    public void ModelDisplayRoundTripsToTheOriginalSkyCatIdentifier()
    {
      var models = new Dictionary<string, string>
      {
        ["3081"] = "IC-9700", ["1035"] = "FT-991A"
      };
      Assert.Equal("IC-9700 (3081)",
        EmbeddedSkyCatModelConverter.DisplayModel("3081", models));
      Assert.Equal("3081",
        EmbeddedSkyCatModelConverter.ParseModel("IC-9700 (3081)", models));
      Assert.Equal("IC-9700 (3081)",
        EmbeddedSkyCatModelConverter.DisplayModel("IC-9700", models));
      Assert.Equal("IC-9700",
        EmbeddedSkyCatModelConverter.ParseModel("IC-9700", models));
      Assert.Equal("custom-radio",
        EmbeddedSkyCatModelConverter.ParseModel("custom-radio", models));
      Assert.Equal("IC-9700 (9999)",
        EmbeddedSkyCatModelConverter.ParseModel("IC-9700 (9999)", models));
    }

    [Fact]
    public void ComPortsSortNumericallyAndKeepUnpluggedSavedSelection()
    {
      var sorted = EmbeddedSkyCatSerialPortConverter.SortPorts(
        new[] { "COM10", "COM2", "com2", "COM1", "COM15" }, "COM9");
      Assert.Equal(new[] { "COM1", "COM2", "COM9", "COM10", "COM15" }, sorted);
      Assert.Empty(EmbeddedSkyCatSerialPortConverter.SortPorts(Array.Empty<string>()));
      Assert.Equal(new[] { "COM17" },
        EmbeddedSkyCatSerialPortConverter.SortPorts(Array.Empty<string>(), "COM17"));
    }

    [Fact]
    public void BothSettingsUseEditablePropertyGridDropdowns()
    {
      var properties = TypeDescriptor.GetProperties(typeof(EmbeddedSkyCatSettings));
      var model = Assert.IsType<EmbeddedSkyCatModelConverter>(
        properties[nameof(EmbeddedSkyCatSettings.Model)]!.Converter);
      var port = Assert.IsType<EmbeddedSkyCatSerialPortConverter>(
        properties[nameof(EmbeddedSkyCatSettings.SerialPort)]!.Converter);

      Assert.True(model.GetStandardValuesSupported());
      Assert.False(model.GetStandardValuesExclusive());
      Assert.True(port.GetStandardValuesSupported());
      Assert.False(port.GetStandardValuesExclusive());

      var settings = new EmbeddedSkyCatSettings { Enabled = true, SerialPort = "COM6" };
      Assert.Null(settings.Validate());
      Assert.Equal("3081", settings.BuildOptions().Model);
      Assert.Equal("COM6", settings.BuildOptions().RigFile);
    }
  }
}
