using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class NativeRotatorCodecTests
{
  private static string Text(byte[]? b) => Encoding.ASCII.GetString(b!);
  [Theory]
  [InlineData(RotatorProtocolKind.Gs232,"W145 045\r")]
  [InlineData(RotatorProtocolKind.EasyCommI,"AZ145.0 EL45.0 UP000 XXX DN000 XXX\n")]
  [InlineData(RotatorProtocolKind.EasyCommII,"AZ145.0 EL45.0\n")]
  [InlineData(RotatorProtocolKind.EasyCommIII,"AZ145.0 EL45.0\n")]
  [InlineData(RotatorProtocolKind.SaebrTrack,"AZ145EL045\n")]
  [InlineData(RotatorProtocolKind.Rotctld,"P 145.0 45.0\n")]
  [InlineData(RotatorProtocolKind.Oz9AarUrc,"{\"GOTO\":[145.0,45.0]}")]
  public void WirePositionsAreCultureIndependent(RotatorProtocolKind p,string expected)
  {
    CultureInfo previous=CultureInfo.CurrentCulture;
    try{CultureInfo.CurrentCulture=new CultureInfo("fr-FR");
      Assert.Equal(expected,Text(NativeRotatorCodec.Point(p,new(145,45))));}
    finally{CultureInfo.CurrentCulture=previous;}
  }
  [Fact]
  public void Rc3040sLanPreservesGs232ByteProtocolAndNeverUsesNRL2Udp()
  {
    Assert.True(NativeRotatorCodec.Supports(RotatorProtocolKind.Rc3040sLan,RotatorTransportKind.Tcp));
    Assert.False(NativeRotatorCodec.Supports(RotatorProtocolKind.Rc3040sLan,RotatorTransportKind.Udp));
    Assert.Equal("W319 036\r",Text(NativeRotatorCodec.Point(RotatorProtocolKind.Rc3040sLan,new(319,36))));
    Assert.Equal("C2\r",Text(NativeRotatorCodec.Query(RotatorProtocolKind.Rc3040sLan)));
    Assert.Equal("S\r",Text(NativeRotatorCodec.Stop(RotatorProtocolKind.Rc3040sLan)));
    Assert.True(NativeRotatorCodec.CanJog(RotatorProtocolKind.Rc3040sLan));
    Assert.True(NativeRotatorCodec.TryParse(RotatorProtocolKind.Rc3040sLan,
      System.Text.Encoding.ASCII.GetBytes("AZ=319 EL=36\r"),out _));
  }

  [Fact] public void Gs232HasExtendedAzimuthAndContinuousMotion()
  {
    Assert.Equal("W450 090\r",Text(NativeRotatorCodec.Point(RotatorProtocolKind.Gs232,new(450,90))));
    Assert.Equal("C2\r",Text(NativeRotatorCodec.Query(RotatorProtocolKind.Gs232)));
    Assert.Equal("S\r",Text(NativeRotatorCodec.Stop(RotatorProtocolKind.Gs232)));
    Assert.Equal("L\r",Text(NativeRotatorCodec.Jog(RotatorDirection.Left)));
    Assert.True(NativeRotatorCodec.TryParse(RotatorProtocolKind.Gs232,Encoding.ASCII.GetBytes("AZ=145 EL=45\r"),out var p));
    Assert.Equal(145,p.Azimuth);
  }
  [Fact] public void SpidBinarySetAndReplyDecode()
  {
    byte[] command=NativeRotatorCodec.Point(RotatorProtocolKind.SpidRot2Prog,new(145,45));
    Assert.Equal(13,command.Length);
    Assert.Equal(0x57,command[0]);
    Assert.Equal(0x2f,command[11]);
    Assert.Equal(0x20,command[12]);
    Assert.Equal(0x1f,NativeRotatorCodec.Query(RotatorProtocolKind.SpidRot2Prog)![11]);
    Assert.Equal(0x0f,NativeRotatorCodec.Stop(RotatorProtocolKind.SpidRot2Prog)![11]);
    byte[] reply={0x57,0x05,0x00,0x05,0x00,0x01,0x04,0x00,0x05,0x00,0x01,0x20};
    Assert.True(NativeRotatorCodec.TryParseSpid(reply,out var p,out _,out _));
    Assert.Equal(145,p.Azimuth);
    Assert.Equal(45,p.Elevation);
    // The last two fields report resolution, not the reply's position scale.
    byte[] differentRes=(byte[])reply.Clone();
    differentRes[5]=2;
    differentRes[10]=4;
    Assert.True(NativeRotatorCodec.TryParseSpid(differentRes,out var samePosition,out int azRes,out int elRes));
    Assert.Equal(145,samePosition.Azimuth);
    Assert.Equal(45,samePosition.Elevation);
    Assert.Equal(2,azRes);
    Assert.Equal(4,elRes);
    // ASCII digit frames are command frames; don't accept them as feedback.
    byte[] ascii=(byte[])reply.Clone();
    ascii[1]=0x35;
    Assert.False(NativeRotatorCodec.TryParseSpid(ascii,out _,out _,out _));
  }
  [Fact] public void EasyCommParsesEachAxisIndependently()
  {
    Assert.True(NativeRotatorCodec.TryParseAxis("AZ=90.5\r"u8,"AZ",out var az));
    Assert.True(NativeRotatorCodec.TryParseAxis("EL12.5\n"u8,"EL",out var el));
    Assert.Equal(90.5,az);
    Assert.Equal(12.5,el);
  }
  [Fact] public void TransportCapabilitiesAndMissingStopsAreExplicit()
  {
    Assert.True(NativeRotatorCodec.Supports(RotatorProtocolKind.Gs232,RotatorTransportKind.Serial));
    Assert.True(NativeRotatorCodec.Supports(RotatorProtocolKind.Gs232,RotatorTransportKind.Tcp));
    Assert.True(NativeRotatorCodec.Supports(RotatorProtocolKind.PstRotator,RotatorTransportKind.Udp));
    Assert.False(NativeRotatorCodec.Supports(RotatorProtocolKind.Rotctld,RotatorTransportKind.Serial));
    Assert.False(NativeRotatorCodec.CanStop(RotatorProtocolKind.SaebrTrack));
    Assert.False(NativeRotatorCodec.CanStop(RotatorProtocolKind.Oz9AarUrc));
    Assert.True(NativeRotatorCodec.CanStop(RotatorProtocolKind.CustomTemplate,"S\r"));
    Assert.False(NativeRotatorCodec.CanJog(RotatorProtocolKind.EasyCommIII));
  }
  [Fact]
  public void RejectsMalformedAndOutOfRangePositionReplies()
  {
    Assert.False(NativeRotatorCodec.TryParse(
      RotatorProtocolKind.Gs232, "AZ=NaN EL=0\\r"u8,out _));
    Assert.False(NativeRotatorCodec.TryParse(
      RotatorProtocolKind.Gs232, "AZ=600 EL=30\\r"u8,out _));
    Assert.False(NativeRotatorCodec.TryParse(
      RotatorProtocolKind.Rotctld, "RPRT -1\\n"u8,out _));
    Assert.False(NativeRotatorCodec.TryParse(
      RotatorProtocolKind.SpidRot2Prog,
      new byte[] {0x57,0x35,0x30,0x35,0x30,0x01,0x30,0x34,0x30,0x35,0x01,0x20},out _));
  }

  [Fact]
  public void CustomTemplateSupportsBothAxisOrders()
  {
    Assert.True(NativeRotatorCodec.TryParse(
      RotatorProtocolKind.CustomTemplate,Encoding.ASCII.GetBytes("EL=45.25 AZ=145.75\r"),
      out var p,"EL=$EL AZ=$AZ\\r"));
    Assert.Equal(145.75,p.Azimuth);
    Assert.Equal(45.25,p.Elevation);
    Assert.Equal("P 145.75 45.25\r",
      Text(NativeRotatorCodec.Point(RotatorProtocolKind.CustomTemplate,new(145.75,45.25),"P $AZ $EL\\r")));
  }

  [Fact]
  public void ProtocolStopAndMovementCapabilitiesAreWellDefined()
  {
    Assert.Equal("SA SE \n",Text(NativeRotatorCodec.Stop(RotatorProtocolKind.EasyCommII)));
    Assert.Equal("<PST><STOP>1</STOP></PST>",
      Text(NativeRotatorCodec.Stop(RotatorProtocolKind.PstRotator)));
    Assert.Equal("S\n",Text(NativeRotatorCodec.Stop(RotatorProtocolKind.Rotctld)));
    Assert.Null(NativeRotatorCodec.Stop(RotatorProtocolKind.Oz9AarUrc));
    Assert.Null(NativeRotatorCodec.Stop(RotatorProtocolKind.SaebrTrack));
    Assert.Equal("AZ\n",Text(NativeRotatorCodec.Query(RotatorProtocolKind.EasyCommIII)));
  }

  [Theory]
  [InlineData(RotatorProtocolKind.EasyCommII,"AZ123.4 EL45.5\r",123.4,45.5)]
  [InlineData(RotatorProtocolKind.Rotctld,"123.4\n45.5\n",123.4,45.5)]
  [InlineData(RotatorProtocolKind.PstRotator,"AZ:123.4 EL:45.5",123.4,45.5)]
  [InlineData(RotatorProtocolKind.Oz9AarUrc,"{\"AZ\":123.4,\"EL\":45.5}",123.4,45.5)]
  public void ProtocolPositionResponses(RotatorProtocolKind p,string reply,double az,double el)
  {
    Assert.True(NativeRotatorCodec.TryParse(p,Encoding.ASCII.GetBytes(reply),out var result));
    Assert.Equal(az,result.Azimuth);Assert.Equal(el,result.Elevation);
  }
}
