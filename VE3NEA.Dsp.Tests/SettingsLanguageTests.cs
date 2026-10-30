using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class SettingsLanguageTests
{
  [Fact]
  public void SwitchingLanguagePreservesPropertyIdentityAndSettingsSerialization()
  {
    var previous = SettingsLanguage.Current;
    try
    {
      SettingsLanguage.Set(UiLanguage.SimplifiedChinese);
      var settings = new Settings();
      var zh = TypeDescriptor.GetProperties(settings).Find("Afc", false);
      Assert.NotNull(zh);
      Assert.Contains("自动频率", zh!.DisplayName);
      Assert.Equal("Afc", zh.Name);
      var afcZh = TypeDescriptor.GetProperties(settings.Afc).Find("InputDeviceId", false);
      Assert.NotNull(afcZh);
      Assert.Contains("AFC 音频输入", afcZh!.DisplayName);

      SettingsLanguage.Set(UiLanguage.English);
      var en = TypeDescriptor.GetProperties(settings).Find("Afc", false);
      Assert.NotNull(en);
      Assert.Contains("FM / CW / SSB AFC", en!.DisplayName);
      var afcEn = TypeDescriptor.GetProperties(settings.Afc).Find("InputDeviceId", false);
      Assert.NotNull(afcEn);
      Assert.Equal("AFC audio input (12 kHz IF)", afcEn!.DisplayName);

      var scope = TypeDescriptor.GetProperties(settings.IcomLanSpectrum)
        .Find("ScopeReferenceLevelDb", false);
      Assert.NotNull(scope);
      scope!.SetValue(settings.IcomLanSpectrum, 3.5);
      Assert.Equal(3.5, settings.IcomLanSpectrum.ScopeReferenceLevelDb);

      settings.Ui.Language = UiLanguage.English;
      string json = JsonConvert.SerializeObject(settings);
      Assert.Contains("\"ScopeReferenceLevelDb\":3.5", json.Replace(" ", ""));
      Assert.Contains("\"Language\":0", json.Replace(" ", ""));
      var restored = JsonConvert.DeserializeObject<Settings>(json)!;
      Assert.Equal(UiLanguage.English, restored.Ui.Language);
      Assert.Equal(3.5, restored.IcomLanSpectrum.ScopeReferenceLevelDb);
    }
    finally { SettingsLanguage.Set(previous); }
  }

  [Fact]
  public void WinFormsPropertyGridCanSwitchLanguageWithoutReplacingSettingsModel()
  {
    Exception? error = null;
    var thread = new Thread(() =>
    {
      var previous = SettingsLanguage.Current;
      try
      {
        SettingsLanguage.Set(UiLanguage.English);
        var edited = new Settings();
        using var grid = new PropertyGrid { SelectedObject = edited };
        var original = grid.SelectedObject;
        Assert.Same(edited, original);
        Assert.Equal("Scope reference level",
          TypeDescriptor.GetProperties(edited.IcomLanSpectrum)
            .Find("ScopeReferenceLevelDb", false)!.DisplayName);

        SettingsLanguage.Set(UiLanguage.SimplifiedChinese);
        grid.SelectedObject = null;
        grid.SelectedObject = edited;
        Assert.Same(original, grid.SelectedObject);
        Assert.Equal("频谱参考电平",
          TypeDescriptor.GetProperties(edited.IcomLanSpectrum)
            .Find("ScopeReferenceLevelDb", false)!.DisplayName);
      }
      catch (Exception ex) { error = ex; }
      finally { SettingsLanguage.Set(previous); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(12)));
    if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
  }

  [Fact]
  public void InvalidSavedLanguageFallsBackToChinese()
  {
    var previous = SettingsLanguage.Current;
    try
    {
      SettingsLanguage.Set((UiLanguage)999);
      Assert.Equal(UiLanguage.SimplifiedChinese, SettingsLanguage.Current);
    }
    finally { SettingsLanguage.Set(previous); }
  }
}
