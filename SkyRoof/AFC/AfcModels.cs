using System;
using System.ComponentModel;
using NAudio.CoreAudioApi;
using VE3NEA;

namespace SkyRoof
{
  public enum AfcIfSource { WasapiCapture, PlaybackLoopback }

  /// <summary>
  /// Frequency tracking is independent of the saved satellite/transponder
  /// offsets and never changes the TX frequency model.
  /// </summary>
  public sealed class AfcSettings
  {
    [Browsable(false)]
    [DisplayName("启用 AFC")]
    [DefaultValue(false)]
    public bool Enabled { get; set; } = false;

    [DisplayName("AFC 音频输入 (12 kHz IF)")]
    [Description("选择承载电台 USB 12 kHz IF 的 Windows 录音设备。若其他程序需要该 USB 声卡的 AF 音频，请使用独立输入通道。")]
    [TypeConverter(typeof(InputSoundcardNameConverter))]
    public string? InputDeviceId { get; set; } =
      Soundcard.GetPreferredSoundcardId(DataFlow.Capture, "USB Audio CODEC", "ICOM");

    [DisplayName("AFC 控制等级")]
    [DefaultValue(AFC.AfcControlMode.Assist)]
    public AFC.AfcControlMode Control { get; set; } = AFC.AfcControlMode.Assist;

    [DisplayName("IF 音频来源")]
    [DefaultValue(AfcIfSource.WasapiCapture)]
    public AfcIfSource InputSource { get; set; } = AfcIfSource.WasapiCapture;

    [DisplayName("远程 IF 播放回环设备")]
    [Description("选择承载远程 12 kHz IF 的播放或虚拟扬声器端点，不可选择仅有解调 AF 的输出。")]
    [TypeConverter(typeof(OutputSoundcardNameConverter))]
    public string? LoopbackDeviceId { get; set; }

    [DisplayName("IF 参考频率 (Hz)")]
    [DefaultValue(12000.0)]
    public double IfReferenceHz { get; set; } = 12000;

    [DisplayName("RF/IF 频率方向 (0=自动，±1=手动)")]
    [Description("0 表示根据稳定信号和 CAT 调谐读回来学习频率方向。只有自动 CAT 修正需要方向判断，辅助模式不要求。")]
    [DefaultValue(0)]
    public int RfIfSign { get; set; } = 0;

    [DisplayName("目标搜索 IF 偏移 (Hz)")]
    [Description("CW/导频参考信号的搜索中心；也可直接点击实时 IF 频谱选择目标。")]
    [DefaultValue(0.0)]
    public double TargetOffsetHz { get; set; } = 0;

    [DisplayName("希望居中的 IF 偏移 (Hz)")]
    [Description("“居中并锁定”要将所选信号移动到的位置，与目标搜索频率相互独立。")]
    [DefaultValue(0.0)]
    public double DesiredOffsetHz { get; set; } = 0;

    [DisplayName("SSB 导频参考可用")]
    [Description("仅当所选 IF 中存在稳定可识别的 CW/导频时才启用。普通抑制载波 SSB 语音无法提供唯一的绝对 AFC 参考。")]
    [DefaultValue(false)]
    public bool SsbPilotEnabled { get; set; } = false;

    [DisplayName("IF 频谱 REF LEVEL (dB)")]
    [Description("12 kHz IF 频谱/瀑布图的显示参考等级，默认 +15 dB。提高数值使画面更暗，降低数值有助于观察较弱信号；只影响显示，不修改 IF 音频、频偏估计、置信度或 CAT 控制。")]
    [DefaultValue(15.0)]
    public double SpectrumRefLevelDb { get; set; } = 15;

    [DisplayName("AFC 最大修正量 (Hz)")]
    [DefaultValue(2000.0)]
    public double MaxCorrectionHz { get; set; } = 2000;

    [DisplayName("FM 写频死区 (Hz)")]
    [DefaultValue(20.0)]
    public double FmDeadbandHz { get; set; } = 20;

    [DisplayName("CW/SSB 写频死区 (Hz)")]
    [DefaultValue(5.0)]
    public double ToneDeadbandHz { get; set; } = 5;

    public override string ToString() => "自适应频率跟踪 AFC";
  }
}

namespace SkyRoof.AFC
{
  public enum AfcSignalMode { FM, CW, SSB }
  public enum AfcControlMode { Off, Assist, Auto }
  public enum AfcLockState { Off, Acquiring, Locked, Coasting, Hold, Lost, Fault }

  public readonly record struct AfcObservation(
    AfcSignalMode Mode,
    DateTime SampleUtc,
    long Generation,
    double IfOffsetHz,
    double SigmaHz,
    double Confidence,
    bool HasAbsoluteReference,
    bool Ambiguous = false,
    float[]? SpectrumDb = null,
    double SpectrumMinHz = -6000,
    double SpectrumStepHz = 12000.0 / 511);

  public readonly record struct AfcSnapshot(
    AfcLockState State,
    AfcSignalMode Mode,
    AfcControlMode Control,
    double ObservedIfOffsetHz,
    double CorrectionHz,
    double ErrorHz,
    double Confidence,
    int MappingSign,
    DateTime LastObservationUtc,
    string Detail);
}
