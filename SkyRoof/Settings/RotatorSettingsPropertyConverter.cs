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

  /// <summary>Historical protocol slot 10 is retired and cannot be selected.</summary>
  public sealed class RotatorProtocolSelectionConverter : EnumConverter
  {
    public RotatorProtocolSelectionConverter()
      : base(typeof(RotatorProtocolKind)) { }
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
      new(Enum.GetValues<RotatorProtocolKind>()
        .Where(p => NativeRotatorCodec.Supports(p,RotatorTransportKind.Tcp) ||
          NativeRotatorCodec.Supports(p,RotatorTransportKind.Serial) ||
          NativeRotatorCodec.Supports(p,RotatorTransportKind.Udp)).ToArray());
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

    private static readonly HashSet<string> Rc3040sOnly = new(StringComparer.Ordinal)
    {
      nameof(RotatorSettings.Rc3040sMotionMode),
      nameof(RotatorSettings.Rc3040sPlannerEnabled),
      nameof(RotatorSettings.Rc3040sMotionHardwareValidated),
      nameof(RotatorSettings.Rc3040sPlanAzSpeed),
      nameof(RotatorSettings.Rc3040sPlanElSpeed),
      nameof(RotatorSettings.Rc3040sPlanAcceleration),
      nameof(RotatorSettings.Rc3040sPlanJerk),
      nameof(RotatorSettings.Rc3040sAtEnabled)
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
      bool rc3040Lan = native && settings.NativeProtocol == RotatorProtocolKind.Rc3040sLan;
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
          _ when Rc3040sOnly.Contains(p.Name) => rc3040Lan,
          nameof(RotatorSettings.NativeTransport) when rc3040Lan => false,
          _ when SerialOnly.Contains(p.Name) => native && serial,
          _ when NativeOnly.Contains(p.Name) => native,
          _ when CustomOnly.Contains(p.Name) => native && custom,
          _ when LegacyOnly.Contains(p.Name) => !native,
          _ => true
        })
        .ToList();

      // PropertyGrid is configured with PropertySort.NoSort. Present the
      // address fields next to the chosen transport, instead of leaving
      // them below all the timeouts and Enabled at the end of the list.
      if (showNetwork)
      {
        var endpoint = visible.Where(p =>
          p.Name is nameof(RotatorSettings.Host) or nameof(RotatorSettings.Port))
          .ToArray();
        visible.RemoveAll(p =>
          p.Name is nameof(RotatorSettings.Host) or nameof(RotatorSettings.Port));

        string after = native ? nameof(RotatorSettings.NativeTransport)
          : nameof(RotatorSettings.DriverMode);
        int index = visible.FindIndex(p => p.Name == after);
        visible.InsertRange(index < 0 ? 0 : index + 1, endpoint);
      }

      return new PropertyDescriptorCollection(visible.ToArray(), true);
    }
  }
}
