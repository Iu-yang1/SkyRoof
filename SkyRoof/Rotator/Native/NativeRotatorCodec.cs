using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SkyRoof.NativeRotator;

public enum RotatorProtocolKind { Gs232, EasyCommI, EasyCommII, EasyCommIII, SpidRot2Prog, SaebrTrack, Rotctld, PstRotator, Oz9AarUrc, CustomTemplate, Rc3040sLan = 11 }
public enum RotatorTransportKind { Serial, Tcp, Udp }
public enum RotatorDirection { Up, Down, Left, Right }
public readonly record struct RotatorPosition(double Azimuth, double Elevation);

public static class NativeRotatorCodec
{
  private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
  private static readonly Regex Number = new(@"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.Compiled);
  private static readonly Regex Gs232 = new(@"AZ\s*=\s*([+-]?\d+(?:\.\d+)?)\s*EL\s*=\s*([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase|RegexOptions.Compiled);
  private static readonly Regex Gs232A = new(@"([+-])0?(\d{3})([+-])0?(\d{3})",RegexOptions.Compiled);
  private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
  private static double Wrap(double a) => (a % 360 + 360) % 360;
  private static string N(double n) => n.ToString("0.##", Inv);
  private static string Expand(string template, RotatorPosition p) =>
    template.Replace("$AZ",N(p.Azimuth)).Replace("$EL",N(p.Elevation))
      .Replace("\\r","\r").Replace("\\n","\n").Replace("\\t","\t");
  public static bool Supports(RotatorProtocolKind protocol, RotatorTransportKind transport) => protocol switch
  {
    RotatorProtocolKind.Rotctld or RotatorProtocolKind.Oz9AarUrc or RotatorProtocolKind.Rc3040sLan => transport == RotatorTransportKind.Tcp,
    RotatorProtocolKind.PstRotator => transport == RotatorTransportKind.Udp,
    RotatorProtocolKind.CustomTemplate => true,
    RotatorProtocolKind.Gs232 or RotatorProtocolKind.EasyCommI or RotatorProtocolKind.EasyCommII or
      RotatorProtocolKind.EasyCommIII or RotatorProtocolKind.SpidRot2Prog or
      RotatorProtocolKind.SaebrTrack => transport is RotatorTransportKind.Serial or RotatorTransportKind.Tcp,
    _ => false
  };
  public static bool CanQuery(RotatorProtocolKind p) => Enum.IsDefined(p) && p is not (RotatorProtocolKind.EasyCommI or RotatorProtocolKind.SaebrTrack);
  public static bool CanStop(RotatorProtocolKind p, string custom = "") => p switch
  {
    RotatorProtocolKind.SaebrTrack or RotatorProtocolKind.Oz9AarUrc => false,
    RotatorProtocolKind.CustomTemplate => !string.IsNullOrWhiteSpace(custom),
    _ => Enum.IsDefined(p)
  };
  public static bool CanJog(RotatorProtocolKind p) => p is RotatorProtocolKind.Gs232 or RotatorProtocolKind.Rc3040sLan;
  public static byte[] Point(RotatorProtocolKind p, RotatorPosition pos, string custom = "", int azRes=1, int elRes=1)
  {
    if (!double.IsFinite(pos.Azimuth) || !double.IsFinite(pos.Elevation))
      throw new ArgumentOutOfRangeException(nameof(pos));
    double az=pos.Azimuth, el=pos.Elevation;
    return p switch
    {
      RotatorProtocolKind.Rc3040sLan => Ascii($"W{(int)Math.Round(Math.Clamp(az,0,450),MidpointRounding.AwayFromZero):000} {(int)Math.Round(Math.Clamp(el,0,180),MidpointRounding.AwayFromZero):000}\r"),
      RotatorProtocolKind.Gs232 => Ascii($"W{(int)Math.Round(az<0?Wrap(az):Math.Min(450,az),MidpointRounding.AwayFromZero):000} {(int)Math.Round(Math.Clamp(el,0,180),MidpointRounding.AwayFromZero):000}\r"),
      RotatorProtocolKind.EasyCommI => Ascii($"AZ{Wrap(az).ToString("0.0",Inv)} EL{Math.Clamp(el,0,180).ToString("0.0",Inv)} UP000 XXX DN000 XXX\n"),
      RotatorProtocolKind.EasyCommII or RotatorProtocolKind.EasyCommIII => Ascii($"AZ{Wrap(az).ToString("0.0",Inv)} EL{Math.Clamp(el,0,180).ToString("0.0",Inv)}\n"),
      RotatorProtocolKind.SpidRot2Prog => Spid(0x2f,az,el,azRes,elRes),
      RotatorProtocolKind.SaebrTrack => Ascii($"AZ{(int)Math.Round(Wrap(az),MidpointRounding.AwayFromZero):000}EL{(int)Math.Round(Math.Clamp(el,0,180),MidpointRounding.AwayFromZero):000}\n"),
      RotatorProtocolKind.Rotctld => Ascii($"P {az.ToString("0.0",Inv)} {Math.Max(0,el).ToString("0.0",Inv)}\n"),
      RotatorProtocolKind.PstRotator => Ascii($"<PST><AZIMUTH>{Wrap(az).ToString("0.0",Inv)}</AZIMUTH><ELEVATION>{Math.Max(0,el).ToString("0.0",Inv)}</ELEVATION></PST>"),
      RotatorProtocolKind.Oz9AarUrc => Ascii($"{{\"GOTO\":[{Wrap(az).ToString("0.0",Inv)},{Math.Max(0,el).ToString("0.0",Inv)}]}}"),
      RotatorProtocolKind.CustomTemplate => Ascii(Expand(custom,pos)),
      _ => throw new ArgumentOutOfRangeException(nameof(p))
    };
  }
  public static byte[]? Stop(RotatorProtocolKind p, string custom="") => p switch
  {
    RotatorProtocolKind.Gs232 or RotatorProtocolKind.Rc3040sLan => Ascii("S\r"),
    RotatorProtocolKind.EasyCommI or RotatorProtocolKind.EasyCommII or RotatorProtocolKind.EasyCommIII => Ascii("SA SE \n"),
    RotatorProtocolKind.SpidRot2Prog => Spid(0x0f),
    RotatorProtocolKind.Rotctld => Ascii("S\n"),
    RotatorProtocolKind.PstRotator => Ascii("<PST><STOP>1</STOP></PST>"),
    RotatorProtocolKind.CustomTemplate when !string.IsNullOrWhiteSpace(custom) => Ascii(Expand(custom,default)),
    _ => null
  };
  public static byte[]? Query(RotatorProtocolKind p,string custom="")=> p switch
  {
    RotatorProtocolKind.Gs232 or RotatorProtocolKind.Rc3040sLan => Ascii("C2\r"),
    RotatorProtocolKind.EasyCommII or RotatorProtocolKind.EasyCommIII => Ascii("AZ\n"),
    RotatorProtocolKind.SpidRot2Prog => Spid(0x1f),
    RotatorProtocolKind.Rotctld => Ascii("p\n"),
    RotatorProtocolKind.Oz9AarUrc => Ascii("{\"POLL\"}"),
    RotatorProtocolKind.CustomTemplate when !string.IsNullOrWhiteSpace(custom) => Ascii(Expand(custom,default)),
    _ => null
  };
  public static byte[] Jog(RotatorDirection direction, RotatorProtocolKind protocol = RotatorProtocolKind.Gs232) => Ascii(direction switch
  { RotatorDirection.Up=>"U", RotatorDirection.Down=>"D", RotatorDirection.Left=>"L", RotatorDirection.Right=>"R",_=>throw new ArgumentOutOfRangeException(nameof(direction)) } +
    "\r");
  private static byte[] Spid(int command,double az=0,double el=0,int azRes=1,int elRes=1)
  {
    var b=new byte[13]; b[0]=0x57;b[11]=(byte)command;b[12]=0x20;
    if (command==0x2f)
    {
      if(azRes is <1 or >100 || elRes is <1 or >100)throw new ArgumentOutOfRangeException(nameof(azRes));
      WriteSpid(b,1,az,azRes); b[5]=(byte)azRes;
      WriteSpid(b,6,el,elRes); b[10]=(byte)elRes;
    }
    return b;
  }
  private static void WriteSpid(byte[] b,int offset,double deg,int resolution)
  {
    int value=checked((int)Math.Round((deg+360)*resolution,MidpointRounding.AwayFromZero));
    if(value is <0 or >9999)throw new ArgumentOutOfRangeException(nameof(deg));
    for(int i=3;i>=0;i--){b[offset+i]=(byte)(0x30+value%10);value/=10;}
  }
  public static bool TryParseSpid(ReadOnlySpan<byte> bytes,out RotatorPosition p,out int azRes,out int elRes)
  {
    p=default;azRes=elRes=1;
    for(int start=0;start<=bytes.Length-12;start++)
    {
      if(bytes[start]!=0x57 || bytes[start+11]!=0x20)continue;
      if(bytes[start+5] is <1 or >100 || bytes[start+10] is <1 or >100)continue;
      // ROT2PROG position replies contain raw numeric digits (0x00..0x09),
      // unlike command frames which contain ASCII digits. The returned pulse
      // fields are expressed in tenths of a degree independently of the
      // per-axis command resolution bytes (see Look4Sat RotatorCodec).
      int az=0,el=0;bool good=true;
      for(int i=1;i<=4;i++){int d=bytes[start+i];if(d>9){good=false;break;}az=az*10+d;}
      for(int i=6;i<=9;i++){int d=bytes[start+i];if(d>9){good=false;break;}el=el*10+d;}
      if(!good)continue;
      azRes=bytes[start+5];elRes=bytes[start+10];
      p=new RotatorPosition(az/10.0-360,el/10.0-360);
      return Valid(p);
    }
    return false;
  }
  public static bool TryParse(RotatorProtocolKind protocol,ReadOnlySpan<byte> input,out RotatorPosition position,string template="")
  {
    position=default;
    if(protocol==RotatorProtocolKind.SpidRot2Prog) return TryParseSpid(input,out position,out _,out _);
    string text=Encoding.ASCII.GetString(input);
    Match? match=null;
    switch(protocol)
    {
      case RotatorProtocolKind.Gs232:
      case RotatorProtocolKind.Rc3040sLan:
        match=Gs232.Match(text);
        if(!match.Success)
        {
          var m=Gs232A.Match(text);
          if(m.Success && double.TryParse(m.Groups[2].Value,NumberStyles.Float,Inv,out double a) &&
             double.TryParse(m.Groups[4].Value,NumberStyles.Float,Inv,out double e))
          {position=new RotatorPosition(m.Groups[1].Value=="-"?-a:a,m.Groups[3].Value=="-"?-e:e);return Valid(position);}
        }
        break;
      case RotatorProtocolKind.EasyCommII:
      case RotatorProtocolKind.EasyCommIII:
      case RotatorProtocolKind.EasyCommI:
        match=Regex.Match(text,@"AZ\s*=?\s*([+-]?\d+(?:\.\d+)?)\s+EL\s*=?\s*([+-]?\d+(?:\.\d+)?)",RegexOptions.IgnoreCase);break;
      case RotatorProtocolKind.SaebrTrack:
        match=Regex.Match(text,@"AZ\s*([+-]?\d+(?:\.\d+)?)\s*EL\s*([+-]?\d+(?:\.\d+)?)",RegexOptions.IgnoreCase);break;
      case RotatorProtocolKind.Rotctld:
        if(Regex.IsMatch(text,@"RPRT\s+-\d+"))return false;
        var n=Number.Matches(text);if(n.Count>=2 && !text.Contains("RPRT",StringComparison.Ordinal))
          match=Regex.Match(text,@"^\s*([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)",RegexOptions.Singleline);break;
      case RotatorProtocolKind.PstRotator:
        match=Regex.Match(text,@"AZ:\s*([+-]?\d+(?:\.\d+)?).*?EL:\s*([+-]?\d+(?:\.\d+)?)",RegexOptions.IgnoreCase|RegexOptions.Singleline);break;
      case RotatorProtocolKind.Oz9AarUrc:
        match=Regex.Match(text,"\"(?:AZ|az)\"\\s*:\\s*([+-]?\\d+(?:\\.\\d+)?).*?\"(?:EL|el)\"\\s*:\\s*([+-]?\\d+(?:\\.\\d+)?)",RegexOptions.Singleline);break;
      case RotatorProtocolKind.CustomTemplate:
        if(template.Length>256 || Regex.Matches(template,@"\$AZ").Count!=1 || Regex.Matches(template,@"\$EL").Count!=1)return false;
        string eTemplate=template.Replace("\\r","\r").Replace("\\n","\n");
        string regex=Regex.Escape(eTemplate).Replace(@"\$AZ",@"([+-]?\d+(?:\.\d+)?)").Replace(@"\$EL",@"([+-]?\d+(?:\.\d+)?)");
        match=Regex.Match(text,regex,RegexOptions.Singleline);
        if(match.Success && eTemplate.IndexOf("$EL",StringComparison.Ordinal)<eTemplate.IndexOf("$AZ",StringComparison.Ordinal))
        {if(double.TryParse(match.Groups[1].Value,NumberStyles.Float,Inv,out double el) &&
            double.TryParse(match.Groups[2].Value,NumberStyles.Float,Inv,out double az))
          {position=new RotatorPosition(az,el);return Valid(position);}
          return false;}
        break;
    }
    if(match==null || !match.Success || match.Groups.Count<3)return false;
    if(!double.TryParse(match.Groups[1].Value,NumberStyles.Float,Inv,out double azimuth) ||
       !double.TryParse(match.Groups[2].Value,NumberStyles.Float,Inv,out double elevation))return false;
    position=new RotatorPosition(azimuth,elevation);
    return Valid(position);
  }
  public static bool TryParseAxis(ReadOnlySpan<byte> bytes,string prefix,out double result)
  {
    result=0;
    var m=Regex.Match(Encoding.ASCII.GetString(bytes).Trim(),
      @"^"+Regex.Escape(prefix)+@"\s*=?\s*([+-]?\d+(?:\.\d+)?)$",RegexOptions.IgnoreCase);
    return m.Success && double.TryParse(m.Groups[1].Value,NumberStyles.Float,Inv,out result) && double.IsFinite(result);
  }
  public static bool Valid(RotatorPosition p) => double.IsFinite(p.Azimuth)&&double.IsFinite(p.Elevation) &&
    p.Azimuth is >=-360 and <=450 && p.Elevation is >=-90 and <=180;
}
