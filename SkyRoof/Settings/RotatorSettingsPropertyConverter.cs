using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using SkyRoof.NativeRotator;

namespace SkyRoof
{
  // Auto=0 is kept solely to deserialize settings files created by
  // SkyRoof 1.57 before this UI migration. It is never offered in the editor.
  public sealed class RotatorDriverModeConverter : EnumConverter
  {
    public RotatorDriverModeConverter() : base(typeof(RotatorDriverMode)) { }

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
      new(new[] { RotatorDriverMode.Native, RotatorDriverMode.LegacyRotctld });
  }

  // The existing Settings.json stays unchanged. Filter only the nested
  // WinForms PropertyGrid descriptors, not the serialized settings object.
  // This also preserves all inactive protocol/transport values when users
  // switch back without losing their previous device configuration.
  public sealed class RotatorSettingsPropertyConverter : ExpandableObjectConverter
  {
    private static readonly HashSet<string> SerialOnly = new(StringComparer.Ordinal)
    {
      nameof(RotatorSettings.NativeSerialPort),
      nameof(RotatorSettings.NativeBaudRate),
      nameof(RotatorSettings.NativeDataBits),
      nameof(RotatorSettings.NativeParity),
      nameof(RotatorSettings.NativeStopBits),
      nameof(RotatorSettings.NativeHandshake),
      nameof(RotatorSettings.NativeDtrEnable),
      nameof(RotatorSettings.NativeRtsEnable)
    };

    private static readonly HashSet<string> NativeOnly = new(StringComparer.Ordinal)
    {
      nameof(RotatorSettings.NativeProtocol),
      nameof(RotatorSettings.NativeTransport),
      nameof(RotatorSettings.NativePollMs),
      nameof(RotatorSettings.NativeReadTimeout),
      nameof(RotatorSettings.NativeAllowOpenLoopMotion)
    };

    private static readonly HashSet<string> CustomOnly = new(StringComparer.Ordinal)
    {
      nameof(RotatorSettings.NativePointTemplate),
      nameof(RotatorSettings.NativeStopTemplate),
      nameof(RotatorSettings.NativeQueryTemplate),
      nameof(RotatorSettings.NativeResponseTemplate)
    };

    private static readonly HashSet<string> LegacyOnly = new(StringComparer.Ordinal)
    {
      nameof(RotatorSettings.Delay),
      nameof(RotatorSettings.LogTraffic),
      nameof(RotatorSettings.ReceiveTimeout),
      nameof(RotatorSettings.PositionCommandResolutionDegrees)
    };

    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;

    public override PropertyDescriptorCollection GetProperties(
      ITypeDescriptorContext? context, object value, Attribute[]? attributes)
    {
      if (value is not RotatorSettings settings)
        return base.GetProperties(context, value, attributes)!;

      bool native = settings.ResolveUseNativeDriver();
      bool serial = settings.NativeTransport == RotatorTransportKind.Serial;
      bool custom = settings.NativeProtocol == RotatorProtocolKind.CustomTemplate;

      // Nondefault remote legacy rotctld endpoints and native TCP/UDP
      // share Host/Port. Direct Serial has no network endpoint to configure.
      bool showNetwork = !native || !serial;
      var properties = TypeDescriptor.GetProperties(settings, attributes ?? Array.Empty<Attribute>(), true);

      var visible = properties.Cast<PropertyDescriptor>()
        .Where(p => p.IsBrowsable)
        .Where(p => p.Name switch
        {
          nameof(RotatorSettings.Host) or nameof(RotatorSettings.Port) => showNetwork,
          _ when SerialOnly.Contains(p.Name) => native && serial,
          _ when NativeOnly.Contains(p.Name) => native,
          _ when CustomOnly.Contains(p.Name) => native && custom,
          _ when LegacyOnly.Contains(p.Name) => !native,
          _ => true
        })
        .ToArray();

      return new PropertyDescriptorCollection(visible, true);
    }
  }
}
