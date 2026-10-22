using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class IcomLanSpectrumSettingsPropertyGridTests
{
  private readonly IcomLanSpectrumSettingsPropertyConverter converter = new();

  private IReadOnlyList<string> Visible(IcomLanSpectrumSettings settings) =>
    converter.GetProperties(null, settings, new Attribute[] { BrowsableAttribute.Yes })
      .Cast<PropertyDescriptor>()
      .Select(p => p.Name)
      .ToList();

  [Fact]
  public void NestedSettingsSectionUsesConditionalConverter()
  {
    var property = TypeDescriptor.GetProperties(typeof(Settings))[
      nameof(Settings.IcomLanSpectrum)];
    Assert.NotNull(property);
    Assert.IsType<IcomLanSpectrumSettingsPropertyConverter>(
      property!.Converter);
  }

  [Fact]
  public void SkyCatOnlyShowsItsTcpPortAndOptionalLanAssistIp()
  {
    var fields = Visible(new IcomLanSpectrumSettings
    {
      Source = IcomLanSpectrumSource.SkyCat
    });
    Assert.Equal(nameof(IcomLanSpectrumSettings.Source), fields[0]);
    Assert.Equal(nameof(IcomLanSpectrumSettings.ControlPath), fields[1]);
    Assert.Contains(nameof(IcomLanSpectrumSettings.RadioAddress), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.SkyCatScopePort), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.AutoStart), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.SerialPort), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanControlPort), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanUsername), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanPassword), fields);
  }

  [Fact]
  public void PassiveRsBa1ShowsCivEndpointButNoSkyCatOrDirectLogin()
  {
    var fields = Visible(new IcomLanSpectrumSettings
    {
      Source = IcomLanSpectrumSource.RsBa1
    });
    Assert.Contains(nameof(IcomLanSpectrumSettings.RadioAddress), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.SerialPort), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.AutoStart), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.SkyCatScopePort), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanControlPort), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanClientName), fields);
  }

  [Fact]
  public void DirectLanShowsAddressPortAndCredentialsButNotAutoStart()
  {
    var fields = Visible(new IcomLanSpectrumSettings
    {
      Source = IcomLanSpectrumSource.DirectLan
    });
    foreach (var name in new[]
    {
      nameof(IcomLanSpectrumSettings.RadioAddress),
      nameof(IcomLanSpectrumSettings.SerialPort),
      nameof(IcomLanSpectrumSettings.DirectLanControlPort),
      nameof(IcomLanSpectrumSettings.DirectLanUsername),
      nameof(IcomLanSpectrumSettings.DirectLanPassword),
      nameof(IcomLanSpectrumSettings.DirectLanClientName)
    })
      Assert.Contains(name, fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.AutoStart), fields);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.SkyCatScopePort), fields);
    Assert.True(fields.ToList().IndexOf(nameof(IcomLanSpectrumSettings.DirectLanControlPort)) <
      fields.ToList().IndexOf(nameof(IcomLanSpectrumSettings.ScopeBand)));
  }

  [Theory]
  [InlineData(IcomLanSpectrumSource.SkyCat)]
  [InlineData(IcomLanSpectrumSource.RsBa1)]
  public void ExplicitDirectControlStillShowsItsCredentials(IcomLanSpectrumSource source)
  {
    var settings = new IcomLanSpectrumSettings
    {
      Source = source,
      ControlPath = IcomScopeControlPath.DirectLan
    };
    var fields = Visible(settings);
    Assert.Contains(nameof(IcomLanSpectrumSettings.DirectLanControlPort), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.DirectLanUsername), fields);
    Assert.Contains(nameof(IcomLanSpectrumSettings.DirectLanPassword), fields);
  }

  [Fact]
  public void CommonScopeControlsAndWaterfallSettingsAlwaysRemainVisible()
  {
    foreach (IcomLanSpectrumSource source in Enum.GetValues<IcomLanSpectrumSource>())
      foreach (IcomScopeControlPath control in Enum.GetValues<IcomScopeControlPath>())
      {
        var fields = Visible(new IcomLanSpectrumSettings
        {
          Source = source,
          ControlPath = control
        });
        foreach (var property in new[]
        {
          nameof(IcomLanSpectrumSettings.ScopeBand),
          nameof(IcomLanSpectrumSettings.ScopeReferenceLevelDb),
          nameof(IcomLanSpectrumSettings.RxSsbEstimatedBandwidthHz),
          nameof(IcomLanSpectrumSettings.SpectrumAverageSweeps),
          nameof(IcomLanSpectrumSettings.WaterfallRows),
          nameof(IcomLanSpectrumSettings.ShowWaterfall)
        })
          Assert.Contains(property, fields);
      }
  }

  [Fact]
  public void SwitchingSourceKeepsHiddenCredentialsAndOriginalJsonValues()
  {
    var settings = new IcomLanSpectrumSettings
    {
      Source = IcomLanSpectrumSource.DirectLan,
      RadioAddress = "192.168.1.25",
      SerialPort = 50002,
      DirectLanUsername = "radio-user",
      DirectLanControlPort = 50001,
      SkyCatScopePort = 4535,
      SpectrumAverageSweeps = 4
    };
    settings.Source = IcomLanSpectrumSource.SkyCat;
    var fields = Visible(settings);
    Assert.DoesNotContain(nameof(IcomLanSpectrumSettings.DirectLanUsername), fields);
    Assert.Equal("radio-user", settings.DirectLanUsername);
    Assert.Equal(50001, settings.DirectLanControlPort);

    var restored = JsonConvert.DeserializeObject<IcomLanSpectrumSettings>(
      JsonConvert.SerializeObject(settings));
    Assert.NotNull(restored);
    Assert.Equal("radio-user", restored!.DirectLanUsername);
    Assert.Equal(50001, restored.DirectLanControlPort);
    Assert.Equal(4535, restored.SkyCatScopePort);
    Assert.Equal(4, restored.SpectrumAverageSweeps);
  }
}
