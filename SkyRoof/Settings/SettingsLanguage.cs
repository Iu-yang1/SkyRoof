using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace SkyRoof
{
  // The persisted language is an enum value, never a translated string.
  public enum UiLanguage { English = 0, SimplifiedChinese = 1 }

  /// <summary>
  /// Localization of PropertyGrid presentation metadata. Property names,
  /// HelpKeyword, converters and JSON serialization are left untouched.
  /// This is intentionally separate from the model objects.
  /// </summary>
  internal static class SettingsLanguage
  {
    internal static UiLanguage Current { get; private set; } = UiLanguage.SimplifiedChinese;
    private static bool registered;
    private static Type[] types = Array.Empty<Type>();
    private static readonly Dictionary<string, string> Chinese = new(StringComparer.OrdinalIgnoreCase)
    {
      // Settings sections.
      ["Orbit / Ephemeris Sources"] = "轨道与星历来源",
      ["User"] = "用户信息", ["Audio"] = "音频",
      ["OutputStream"] = "音频输出流", ["Telemetry"] = "卫星遥测",
      ["Announcements"] = "语音播报", ["Waterfall"] = "瀑布图",
      ["CAT Control"] = "CAT 电台控制", ["Icom LAN Spectrum"] = "ICOM LAN 频谱",
      ["ONVIF Camera"] = "ONVIF 摄像头", ["Rotator Control"] = "天线旋转器控制",
      ["SoapyRemote"] = "远程 SDR", ["Amsat Satellite Status"] = "AMSAT 卫星状态",
      ["QSO Entry"] = "通联日志", ["FT4 Console"] = "FT4 控制台",
      ["CW Console"] = "CW 控制台", ["FM / CW / SSB AFC"] = "FM / CW / SSB 自动频率控制",
      ["Transverter"] = "变频器", ["OrbitSources"] = "轨道来源",
      ["Satellites"] = "卫星", ["Sdr"] = "SDR", ["Amsat"] = "AMSAT",
      ["QsoEntry"] = "QSO 日志", ["Ft4Console"] = "FT4 控制台",
      ["CwConsole"] = "CW 控制台", ["Afc"] = "自动频率控制 AFC",
      ["Rotator"] = "天线旋转器", ["Cat"] = "CAT 电台控制",
      ["IcomLanSpectrum"] = "ICOM LAN 频谱",
      // Common model labels used throughout the settings PropertyGrid.
      ["Enabled"] = "启用", ["Auto start"] = "自动启动", ["AutoStart"] = "自动启动",
      ["Host"] = "主机地址", ["Port"] = "端口",
      ["Name"] = "名称", ["Device"] = "设备", ["Mode"] = "模式",
      ["Volume"] = "音量", ["Gain"] = "增益",
      ["Sample rate"] = "采样率", ["Bandwidth"] = "带宽",
      ["Frequency"] = "频率", ["Timeout"] = "超时",
      ["Receive timeout"] = "接收超时", ["Poll interval"] = "轮询间隔",
      ["Delay"] = "延迟", ["Theme"] = "主题",
      ["Latitude"] = "纬度", ["Longitude"] = "经度",
      ["Altitude"] = "海拔高度", ["Call"] = "呼号",
      ["Square"] = "网格定位", ["Callsign"] = "呼号",
      ["Radio IPv4 address"] = "电台 IPv4 地址",
      ["CI-V LAN port"] = "CI-V LAN 端口",
      ["Scope band"] = "频谱接收机", ["Scope source"] = "频谱数据来源",
      ["Scope control path"] = "频谱控制路径",
      ["Scope edge number"] = "固定频谱边界编号",
      ["Scope reference level"] = "频谱参考电平",
      ["Scope sweep speed"] = "频谱扫描速度",
      ["Scope during TX"] = "发射时显示频谱",
      ["CENTER type display"] = "CENTER 显示基准",
      ["Scope VBW"] = "频谱视频带宽 VBW",
      ["Fixed/scroll marker position"] = "固定/滚动模式标记位置",
      ["SkyCAT scope TCP port"] = "SkyCAT 频谱 TCP 端口",
      ["Direct LAN control port"] = "直连 LAN 控制端口",
      ["Direct LAN username"] = "直连 LAN 用户名",
      ["Direct LAN password"] = "直连 LAN 密码",
      ["Direct LAN client name"] = "直连 LAN 客户端名",
      ["Spectrum averaging"] = "频谱平均次数",
      ["Spectrum smoothing"] = "频谱平滑点数",
      ["Spectrum height"] = "频谱显示高度",
      ["Waterfall history"] = "瀑布图历史行数",
      ["Show waterfall"] = "显示瀑布图",
      ["Waterfall brightness"] = "瀑布图亮度",
      ["Waterfall contrast"] = "瀑布图对比度",
      ["Waterfall palette"] = "瀑布图配色",
      ["Estimated RX SSB filter width (Hz)"] = "SSB 接收滤波带宽估计 (Hz)",
      ["Estimated RX SSB-D filter width (Hz)"] = "SSB-D 接收滤波带宽估计 (Hz)",
      ["Estimated RX CW filter width (Hz)"] = "CW 接收滤波带宽估计 (Hz)",
      ["Estimated RX FM/FM-D filter width (Hz)"] = "FM 接收滤波带宽估计 (Hz)",
      ["IF reference frequency (Hz)"] = "IF 参考频率 (Hz)",
      // Localized labels for the main settings sections and device subpanels.
      ["Grid Square"] = "网格定位",
      ["Delay (ms)"] = "延迟 (ms)",
      ["Log Traffic"] = "记录通信数据",
      ["Send Timeout"] = "发送超时",
      ["Receive Timeout"] = "接收超时",
      ["Reconnect Delay"] = "重连延迟",
      ["Ignore Dial Knob"] = "忽略电台调谐旋钮",
      ["Tuning Step (Hz)"] = "调谐步进 (Hz)",
      ["Physical PTT Key"] = "物理 PTT 按键",
      ["Suppress PTT Key"] = "禁用 PTT 按键",
      ["Embedded SkyCAT"] = "内嵌 SkyCAT",
      ["RX CAT"] = "接收 CAT",
      ["TX CAT"] = "发射 CAT",
      ["TCP Port"] = "TCP 端口",
      ["Use Embedded SkyCAT"] = "使用内嵌 SkyCAT",
      ["Show Corrected Frequency"] = "显示校正频率",
      ["Controller Driver"] = "控制器驱动",
      ["Native Protocol"] = "原生控制协议",
      ["Native Transport"] = "原生通信传输",
      ["RC-3040S Motion Profile"] = "RC-3040S 运动曲线",
      ["RC-3040S Motion Planner Enabled"] = "启用 RC-3040S 轨迹规划",
      ["RC-3040S AZ Planned Speed (deg/s)"] = "RC-3040S 方位角计划速度 (°/s)",
      ["RC-3040S EL Planned Speed (deg/s)"] = "RC-3040S 仰角计划速度 (°/s)",
      ["RC-3040S Acceleration (deg/s²)"] = "RC-3040S 加速度 (°/s²)",
      ["RC-3040S Jerk (deg/s³)"] = "RC-3040S 加加速度 (°/s³)",
      ["RC-3040S Motion Hardware Validated"] = "RC-3040S 运动硬件已验证",
      ["RC-3040S Independent Angle Verification"] = "RC-3040S 独立角度核验",
      ["RC-3040S AT Readback"] = "RC-3040S AT 配置读回",
      ["Serial Port"] = "串行端口",
      ["Baud Rate"] = "波特率",
      ["Serial Data Bits"] = "串口数据位",
      ["Serial Parity"] = "串口校验位",
      ["Serial Stop Bits"] = "串口停止位",
      ["Serial Handshake"] = "串口握手",
      ["Serial DTR Enabled"] = "启用串口 DTR",
      ["Serial RTS Enabled"] = "启用串口 RTS",
      ["Native Poll Interval (ms)"] = "原生轮询间隔 (ms)",
      ["Native Response Timeout (ms)"] = "原生响应超时 (ms)",
      ["Native Feedback Reconnect Grace (ms)"] = "反馈断线重连宽限 (ms)",
      ["Allow Open Loop Motion"] = "允许开环旋转",
      ["Custom Point Command"] = "自定义角度指令",
      ["Custom STOP Command"] = "自定义停止指令",
      ["Custom Query Command"] = "自定义查询指令",
      ["Custom Response Format"] = "自定义响应格式",
      ["Rotator Network Host"] = "旋转器网络主机",
      ["Rotator Network Port"] = "旋转器网络端口",
      ["Minimum Azimuth"] = "最小方位角",
      ["Maximum Azimuth"] = "最大方位角",
      ["Azimuth Offset"] = "方位角偏置",
      ["Minimum Elevation"] = "最小仰角",
      ["Maximum Elevation"] = "最大仰角",
      ["Elevation Offset"] = "仰角偏置",
      ["Tracking Step Size"] = "追踪步进",
      ["Position Command Resolution (deg)"] = "位置指令分辨率 (°)",
      ["Position Jump Limit"] = "位置突变限制",
      ["Parking Azimuth"] = "停靠方位角",
      ["Parking Elevation"] = "停靠仰角",
      ["Enable Receive"] = "启用接收",
      ["Audio Source"] = "音频来源",
      ["Radio / USB Capture Device (RX)"] = "电台 / USB 接收设备",
      ["RS-BA1 Playback Endpoint (loopback)"] = "RS-BA1 回环播放端点",
      ["Spectrum Cleanup"] = "频谱清理",
      ["SkyCAT CW Keyer Port"] = "SkyCAT CW 键控端口",
      ["CW Message Macros"] = "CW 宏消息",
      ["WSJT-X UDP Packets"] = "WSJT-X UDP 数据包",
      ["Decoded Messages"] = "解码消息",
      ["Soundcard"] = "声卡",
      ["Watchdog"] = "看门狗",
      ["Use XIT"] = "使用 XIT",
      ["UDP Port"] = "UDP 端口",
      ["Save to File"] = "保存到文件",
      ["Font Size"] = "字体大小",
      ["Background Colors"] = "背景颜色",
      ["Separator"] = "分隔符",
      ["Transmitted Message"] = "发射消息",
      ["Message To Me"] = "发给我的消息",
      ["Message From Me"] = "我发出的消息",
      ["CQ Word"] = "CQ 字样",
      ["Hot Item"] = "高亮项目",
      ["Quality"] = "画面质量",
      ["Show performance counters"] = "显示性能计数器",
      ["Recorder Audio Source"] = "录音音频来源",
      ["Recorder USB AF Capture Device"] = "USB AF 录音设备",
      ["Recorder RS-BA1 Playback Device"] = "RS-BA1 播放设备",
      ["Speaker Audio Device"] = "扬声器音频设备",
      ["FM Squelch"] = "FM 静噪",
      ["SSTV Auto-Save Filter"] = "SSTV 自动保存滤波器",
      ["KISS Server"] = "KISS 服务器",
      ["SatNOGS Upload"] = "SatNOGS 上传",
      ["Satellite List URL"] = "卫星列表网址",
      ["Transmitter List URL"] = "转发器列表网址",
      ["CelesTrak OMM CSV URL"] = "CelesTrak OMM CSV 网址",
      ["Manual Orbit Source URLs"] = "手动轨道来源网址",
      ["AutoTLE URL"] = "AutoTLE 网址",
      ["SatNOGS Orbit URL"] = "SatNOGS 轨道网址",
      ["DE440s Download Sources"] = "DE440s 下载来源",
      ["DE421 Download Sources"] = "DE421 下载来源",
      ["JPL Ephemeris Kernel"] = "JPL 星历内核",
      ["Custom JPL BSP File"] = "自定义 JPL BSP 文件",
      ["Show Solar-System Targets"] = "显示太阳系目标",
      ["Auto-download JPL Kernel"] = "自动下载 JPL 内核",
      ["Gain, dB"] = "增益 (dB)",
      ["VAC Device"] = "虚拟音频线设备",
      ["Enable Embedded SkyCAT"] = "启用内嵌 SkyCAT",
      ["Radio Model"] = "电台型号",
      ["CI-V Serial Port"] = "CI-V 串口",
      ["Serial Baud Rate"] = "串口波特率",
      ["Main CAT TCP Port"] = "主 CAT TCP 端口",
      ["WSJT-X Proxy Enabled"] = "启用 WSJT-X 代理",
      ["WSJT-X TCP Port"] = "WSJT-X TCP 端口",
      ["Spectrum TCP Port"] = "频谱 TCP 端口",
      ["Switch Enabled"] = "启用远程切换",
      ["Switch TCP Port"] = "切换服务 TCP 端口",
      ["CW Keyer Enabled"] = "启用 CW 键控",
      ["CW Keyer TCP Port"] = "CW 键控 TCP 端口",
      // AFC property grid. The source class intentionally carries Chinese
      // metadata, which is translated in the English branch below.
      ["Enabled AFC"] = "启用 AFC"
    };
    private static readonly Dictionary<string, string> AfcEnglish = new(StringComparer.Ordinal)
    {
      ["InputDeviceId"] = "AFC audio input (12 kHz IF)",
      ["Control"] = "AFC control level",
      ["InputSource"] = "IF audio source",
      ["LoopbackDeviceId"] = "Remote IF playback loopback device",
      ["IfReferenceHz"] = "IF reference frequency (Hz)",
      ["RfIfSign"] = "RF/IF frequency polarity (0=Auto, ±1=Manual)",
      ["TargetOffsetHz"] = "Target IF search offset (Hz)",
      ["DesiredOffsetHz"] = "Desired centered IF offset (Hz)",
      ["SsbPilotEnabled"] = "SSB pilot reference available",
      ["SpectrumRefLevelDb"] = "IF spectrum REF LEVEL (dB)",
      ["MaxCorrectionHz"] = "Maximum AFC correction (Hz)",
      ["FmDeadbandHz"] = "FM tuning deadband (Hz)",
      ["ToneDeadbandHz"] = "CW/SSB tuning deadband (Hz)",
      ["Enabled"] = "Enable AFC"
    };
    private static readonly Dictionary<string, string> AfcEnglishHelp = new(StringComparer.Ordinal)
    {
      ["InputDeviceId"] = "Windows recording device carrying the radio's 12 kHz USB IF. Use a separate input if another application needs AF audio.",
      ["LoopbackDeviceId"] = "Playback or virtual speaker endpoint carrying remote 12 kHz IF, not a demodulated AF-only output.",
      ["RfIfSign"] = "Zero learns polarity from stable signals and CAT readback. Only automatic CAT correction requires a known polarity.",
      ["TargetOffsetHz"] = "Search center for a CW/pilot reference. You can also click the live IF spectrum to select a target.",
      ["DesiredOffsetHz"] = "The offset to which Center & Lock moves the selected signal; independent of the target search frequency.",
      ["SsbPilotEnabled"] = "Enable only for a stable recognizable CW/pilot. Suppressed-carrier speech SSB cannot establish absolute AFC reference.",
      ["SpectrumRefLevelDb"] = "Display reference for the 12 kHz IF spectrum/waterfall only. Does not change IF audio, AFC estimates or CAT tuning."
    };

    internal static string Text(string chinese, string english) =>
      Current == UiLanguage.SimplifiedChinese ? chinese : english;

    internal static void Set(UiLanguage language)
    {
      if (!Enum.IsDefined(typeof(UiLanguage), language))
        language = UiLanguage.SimplifiedChinese;
      EnsureRegistered();
      if (Current == language) return;
      Current = language;
      foreach (Type type in types) TypeDescriptor.Refresh(type);
    }

    internal static void EnsureRegistered()
    {
      if (registered) return;
      registered = true;
      types = typeof(Settings).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract &&
          t.Namespace != null && t.Namespace.StartsWith("SkyRoof", StringComparison.Ordinal) &&
          (t == typeof(Settings) || t == typeof(AfcSettings) ||
           t.Name.EndsWith("Settings", StringComparison.Ordinal)))
        .ToArray();
      foreach (Type type in types)
        TypeDescriptor.AddProvider(
          new LocalizedProvider(TypeDescriptor.GetProvider(type)), type);
    }

    private static string DisplayFor(PropertyDescriptor item)
    {
      if (item.ComponentType == typeof(AfcSettings) &&
          Current == UiLanguage.English &&
          AfcEnglish.TryGetValue(item.Name, out string? en)) return en;
      if (Current == UiLanguage.English) return item.DisplayName;
      if (Chinese.TryGetValue(item.DisplayName, out string? zh)) return zh;
      if (Chinese.TryGetValue(item.Name, out zh)) return zh;
      return item.DisplayName;
    }

    private static string DescriptionFor(PropertyDescriptor item)
    {
      if (item.ComponentType == typeof(AfcSettings) &&
          Current == UiLanguage.English &&
          AfcEnglishHelp.TryGetValue(item.Name, out string? en)) return en;
      return item.Description;
    }

    private sealed class LocalizedProvider : TypeDescriptionProvider
    {
      private readonly TypeDescriptionProvider parent;
      internal LocalizedProvider(TypeDescriptionProvider parent) => this.parent = parent;

      public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance) =>
        new LocalizedDescriptor(parent.GetTypeDescriptor(objectType, instance));
    }

    private sealed class LocalizedDescriptor : CustomTypeDescriptor
    {
      internal LocalizedDescriptor(ICustomTypeDescriptor? parent) : base(parent) { }

      public override PropertyDescriptorCollection GetProperties() =>
        Wrap(base.GetProperties());

      public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes) =>
        Wrap(base.GetProperties(attributes));

      private static PropertyDescriptorCollection Wrap(PropertyDescriptorCollection items) =>
        new(items.Cast<PropertyDescriptor>().Select(p =>
          (PropertyDescriptor)new LocalizedProperty(p)).ToArray(), true);
    }

    private sealed class LocalizedProperty : PropertyDescriptor
    {
      private readonly PropertyDescriptor source;
      internal LocalizedProperty(PropertyDescriptor source)
        : base(source, source.Attributes.Cast<Attribute>().ToArray()) =>
        this.source = source;

      public override string DisplayName => DisplayFor(source);
      public override string Description => DescriptionFor(source);
      public override bool IsReadOnly => source.IsReadOnly;
      public override Type ComponentType => source.ComponentType;
      public override Type PropertyType => source.PropertyType;
      public override TypeConverter Converter => source.Converter;
      public override object? GetEditor(Type editorBaseType) => source.GetEditor(editorBaseType);
      public override bool CanResetValue(object component) => source.CanResetValue(component);
      public override object? GetValue(object? component) => source.GetValue(component);
      public override void ResetValue(object component) => source.ResetValue(component);
      public override void SetValue(object? component, object? value) => source.SetValue(component, value);
      public override bool ShouldSerializeValue(object component) => source.ShouldSerializeValue(component);
      public override void AddValueChanged(object component, EventHandler handler) =>
        source.AddValueChanged(component, handler);
      public override void RemoveValueChanged(object component, EventHandler handler) =>
        source.RemoveValueChanged(component, handler);
    }
  }
}
