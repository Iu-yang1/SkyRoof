using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwNewDecoderTests
  {
    private static readonly DateTime EndUtc=
      new(2026,10,29,12,15,0,DateTimeKind.Utc);
    private static CwSignalTrack Track(int id,double freq,
      CwKeyingStatus keying=CwKeyingStatus.Verified)=>
      new(id,freq,18,0,EndUtc.AddSeconds(-4),EndUtc,
        true,true,KeyingStatus:keying);

    [Fact]
    public void DefaultsToDeepCw_AndAllowsExplicitNewCwWithoutModel()
    {
      new SkyRoof.CwConsoleSettings().DecoderMode.Should()
        .Be(CwDecoderMode.DeepCW);
      var options=new CwReceiveWorkerOptions
        {DecoderMode=CwDecoderMode.NewCW};
      using var decoder=new NewCwInferenceSession(options);
      const int rate=8000;
      var audio=new float[rate*6];
      var snapshot=new CwAudioSnapshot(rate,EndUtc,audio.Length,audio);
      CwContinuousDecodeBatch batch=decoder.Decode(
        snapshot,[Track(1,800)]);
      batch.LaneResults.Should().HaveCount(1);
      batch.LaneResults[0].Text.Should().BeEmpty();
      batch.Transcripts.Should().HaveCount(1);
    }

    [Fact]
    public void StandaloneHsmmPathDecodesMorseWithoutOnnx()
    {
      const int rate=8000;
      float[] audio=new float[rate*6];
      // A continuous carrier keyed as SOS (20 WPM), with realistic
      // 1/3 dot/dash and 1/3 intra/inter-character spacings.
      int cursor=rate*2;
      double phase=0;
      void Advance(bool key,int count)
      {
        for(int i=0;i<count;i++,cursor++)
        {
          if(cursor>=audio.Length)return;
          phase+=2*Math.PI*800/rate;
          audio[cursor]=key?(float)(0.4*Math.Sin(phase)):0;
        }
      }
      foreach(string letter in new[]{"...","---","..."})
      {
        for(int j=0;j<letter.Length;j++)
        {
          Advance(true,letter[j]=='-'?rate*18/100:rate*6/100);
          if(j+1<letter.Length)Advance(false,rate*6/100);
        }
        Advance(false,rate*18/100);
      }
      var decoded=new CwNewDecoder().Decode(audio,rate,EndUtc,
        [Track(7,800)]);
      decoded.Should().HaveCount(1);
      decoded[0].OutputFrameCount.Should().BeGreaterThan(100);
      decoded[0].Symbols.Should().NotBeNull();
      // A synthetic keying sample is decoded locally; no ONNX metadata,
      // model manager, network fetch or skycatd is used.
      float[] keying=CwHsmmDecoder.EstimateEnvelope(audio,rate,800);
      CwHsmmResult direct=CwHsmmDecoder.Decode(keying,0.010);
      decoded[0].Text.Should().Contain("SOS",
        $"offline morse={direct.Text} evidence={direct.EvidenceScore:F3} wpm={direct.Wpm:F0} frames={direct.InputFrames}; decoded='{decoded[0].Text}'");
    }

    [Fact]
    public void NoVerifiedStation_NoDecoderOutputOrFalseText()
    {
      float[] silence=new float[8000*3];
      var decoder=new CwNewDecoder();
      decoder.Decode(silence,8000,EndUtc,
        [Track(1,800,CwKeyingStatus.Candidate)])
        .Should().BeEmpty();
      decoder.Decode(silence,8000,EndUtc,[Track(2,800)])
        .Single().Text.Should().BeEmpty();
    }

    [Fact]
    public void InsufficientlySeparatedStationIsNotGivenOtherStationsText()
    {
      const int rate=8000;
      var audio=new float[rate*3];
      // No coherent separation is possible for coincident carriers.
      // A duplicate track must not receive a cloned transcript.
      var result=new CwNewDecoder().Decode(audio,rate,EndUtc,
        [Track(1,800),Track(2,802)]);
      result.Should().HaveCount(2);
      result.Should().OnlyContain(x=>x.Text=="");
    }

    [Fact]
    public void HistorySeparatesModesAcrossSameAudioGeneration()
    {
      string folder=Path.Combine(Path.GetTempPath(),
        "SkyRoof-DecoderModes-"+Guid.NewGuid().ToString("N"));
      using var archive=new CwTranscriptArchive(folder);
      CwTranscriptSnapshot Snap(string text)=>
        new(4,5,text,"",[],EndUtc);
      archive.Append(8,[Snap("CQ")],CwDecoderMode.DeepCW);
      archive.Append(8,[Snap("SOS")],CwDecoderMode.NewCW);
      IReadOnlyList<CwArchivedTranscript> entries=archive.GetAll();
      entries.Should().HaveCount(2);
      entries.Single(x=>x.Mode==CwDecoderMode.DeepCW).CommittedText
        .Should().Be("CQ");
      entries.Single(x=>x.Mode==CwDecoderMode.NewCW).CommittedText
        .Should().Be("SOS");
    }
  }
}
