using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace SkyRoof
{
  /// <summary>
  /// Keep the Icom LAN Spectrum configuration serialized in full, but present
  /// only parameters relevant to the chosen scope source and control path.
  /// This is a UI-only projection: switching source never resets credentials,
  /// TCP ports, or display preferences.
  /// </summary>
  public sealed class IcomLanSpectrumSettingsPropertyConverter :
    ExpandableObjectConverter
  {
    private static readonly HashSet<string> DirectLanOnly =
      new(StringComparer.Ordinal)
      {
        nameof(IcomLanSpectrumSettings.DirectLanControlPort),
        nameof(IcomLanSpectrumSettings.DirectLanUsername),
        nameof(IcomLanSpectrumSettings.DirectLanPassword),
        nameof(IcomLanSpectrumSettings.DirectLanClientName)
      };

    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) =>
      true;

    public override PropertyDescriptorCollection GetProperties(
      ITypeDescriptorContext? context, object value, Attribute[]? attributes)
    {
      if (value is not IcomLanSpectrumSettings settings)
        return base.GetProperties(context, value, attributes)!;

      bool skycat = settings.Source == IcomLanSpectrumSource.SkyCat;
      bool directSource = settings.Source == IcomLanSpectrumSource.DirectLan;
      // Source and scope control path are deliberately independent. A user
      // choosing explicit DirectLan control still needs to see its settings,
      // even when passive RS-BA1 supplies the waveform.
      bool directControl = settings.ControlPath == IcomScopeControlPath.DirectLan;
      bool showDirect = directSource || directControl;

      var properties = TypeDescriptor.GetProperties(
        settings, attributes ?? Array.Empty<Attribute>(), true);

      var visible = properties.Cast<PropertyDescriptor>()
        .Where(p => p.IsBrowsable)
        .Where(p => p.Name switch
        {
          nameof(IcomLanSpectrumSettings.SerialPort) => !skycat,
          nameof(IcomLanSpectrumSettings.SkyCatScopePort) => skycat,
          nameof(IcomLanSpectrumSettings.AutoStart) => !directSource,
          _ when DirectLanOnly.Contains(p.Name) => showDirect,
          _ => true
        })
        .ToList();

      // The settings dialog uses PropertySort.NoSort. Group the source and
      // its actual endpoint fields together, as in Rotator Control.
      // RadioAddress is also an optional IP filter for SkyCAT LAN assist and
      // passive RS-BA1, so it intentionally remains accessible in all modes.
      string[] connectionOrder =
      {
        nameof(IcomLanSpectrumSettings.Source),
        nameof(IcomLanSpectrumSettings.ControlPath),
        nameof(IcomLanSpectrumSettings.RadioAddress),
        nameof(IcomLanSpectrumSettings.SerialPort),
        nameof(IcomLanSpectrumSettings.SkyCatScopePort),
        nameof(IcomLanSpectrumSettings.DirectLanControlPort),
        nameof(IcomLanSpectrumSettings.DirectLanUsername),
        nameof(IcomLanSpectrumSettings.DirectLanPassword),
        nameof(IcomLanSpectrumSettings.DirectLanClientName),
        nameof(IcomLanSpectrumSettings.AutoStart)
      };

      var connectionProperties = connectionOrder
        .Select(name => visible.FirstOrDefault(p => p.Name == name))
        .Where(p => p != null)
        .Cast<PropertyDescriptor>()
        .ToArray();

      visible.RemoveAll(p => connectionOrder.Contains(
        p.Name, StringComparer.Ordinal));
      visible.InsertRange(0, connectionProperties);

      return new PropertyDescriptorCollection(visible.ToArray(), true);
    }
  }
}
