using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwMhtExperimentalTests
  {
    private static readonly DateTime Now=new(
      2026,10,29,12,0,0,DateTimeKind.Utc);

    private static CwSignalTrack Track(int id,double hz,double snr=20)=>
      new(id,hz,snr,0,Now,Now,true,true);

    [Fact]
    public async Task CwCloseStop_WaitIsBoundedButNeverTreatsTimeoutAsAck()
    {
      var unfinished=new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      bool confirmed=await CwCloseStopPolicy.AwaitVerifiedStopAsync(
        unfinished.Task,TimeSpan.FromMilliseconds(40));
      confirmed.Should().BeFalse();
      unfinished.Task.IsCompleted.Should().BeFalse();
      unfinished.SetResult(true);
      (await CwCloseStopPolicy.AwaitVerifiedStopAsync(
        unfinished.Task,TimeSpan.FromMilliseconds(50))).Should().BeTrue();
    }

    [Fact]
    public async Task CwCloseStop_PropagatesFailedStop()
    {
      Func<Task> action=async ()=>
      {
        await Task.Yield();
        throw new IOException("SkyCAT lease failed");
      };
      Func<Task> verify=async ()=>await CwCloseStopPolicy
        .AwaitVerifiedStopAsync(action(),TimeSpan.FromSeconds(1));
      await verify.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public void TranscriptArchive_NeverOverwritesCommittedHistory()
    {
      string root=Path.Combine(Path.GetTempPath(),
        "SkyRoof-CwMht-"+Guid.NewGuid().ToString("N"));
      using var archive=new CwTranscriptArchive(root);
      CwTranscriptSnapshot Snap(string committed,int hint=9)=>
        new(1,hint,committed,"temporary",
          Array.Empty<CwTranscriptSymbol>(),Now);
      archive.Append(1,[Snap("CQ")]);
      archive.Append(1,[Snap("CQ DE")]);
      archive.Append(1,[Snap("C")]); // stale decoder window
      archive.Append(2,[Snap("QSO")]); // new PCM generation
      var entries=archive.GetAll();
      entries.Should().HaveCount(2);
      entries.Single(x=>x.TimelineGeneration==1)
        .CommittedText.Should().Be("CQ DE");
      entries.Single(x=>x.TimelineGeneration==2)
        .CommittedText.Should().Be("QSO");
    }

    [Fact]
    public async Task TranscriptArchive_WritesUtcJsonlOutsideAudioCallback()
    {
      string root=Path.Combine(Path.GetTempPath(),
        "SkyRoof-CwDisk-"+Guid.NewGuid().ToString("N"));
      using var archive=new CwTranscriptArchive(root);
      archive.Append(10,[new CwTranscriptSnapshot(1,3,"TEST","?",
        [],Now)]);
      string destination=Path.Combine(root,"cw-2026-10-29.jsonl");
      for(int i=0;i<100 && (!File.Exists(destination) ||
         new FileInfo(destination).Length==0);i++)
        await Task.Delay(20);
      File.Exists(destination).Should().BeTrue();
      string text=File.ReadAllText(destination);
      text.Should().Contain("TEST");
      text.Should().Contain("SessionId");
    }

    [Fact]
    public void HypothesisGate_RejectsBirdieAndAdmitsRepeatedKeying()
    {
      var gate=new CwStationHypothesisManager();
      var track=Track(9,820);
      var carrier=new CwKeyingAssessment(false,0,1,0,0);
      for(int i=0;i<20;i++)
        gate.Update(track,carrier,Now.AddMilliseconds(120*i))
          .Should().BeFalse();
      var keyed=new CwKeyingAssessment(true,13,0.42,7,4);
      bool admitted=false;
      for(int i=20;i<45;i++)
        admitted=gate.Update(track,keyed,Now.AddMilliseconds(120*i));
      admitted.Should().BeTrue();
      gate.Reset();
      gate.Update(track,carrier,Now.AddSeconds(8)).Should().BeFalse();
    }

    [Fact]
    public void CoherentSeparation_RecoversTwoIndependentTones()
    {
      const int rate=8000;
      const int count=rate;
      const double first=800,second=815;
      float[] mix=new float[count];
      for(int i=0;i<count;i++)
      {
        double t=i/(double)rate;
        mix[i]=(float)(Math.Sin(2*Math.PI*first*t)+
          0.4*Math.Sin(2*Math.PI*second*t+0.9));
      }
      bool ok=CwCoherentMultitoneSeparator.TrySeparate(
        mix,rate,[Track(1,first),Track(2,second)],
        out var sources);
      ok.Should().BeTrue();
      sources.Keys.Should().BeEquivalentTo([1,2]);
      double mean1=sources[1].Select(x=>(double)x*x).Average();
      double mean2=sources[2].Select(x=>(double)x*x).Average();
      mean1.Should().BeGreaterThan(mean2*3);
      mean2.Should().BeGreaterThan(0.02);
    }

    [Fact]
    public void CoherentSeparation_RejectsSubResolutionOrIdenticalTones()
    {
      float[] mix=new float[8000];
      CwCoherentMultitoneSeparator.TrySeparate(
        mix,8000,[Track(1,800),Track(2,801)],
        out _).Should().BeFalse();
    }

    [Fact]
    public void Hsmm_RecognizesMorseTimingAndPreservesNeuralAuthority()
    {
      var frames=new List<float>();
      void Add(bool keyed,int length)
      {
        for(int i=0;i<length;i++)
          frames.Add(keyed?0.99f:0.01f);
      }
      Add(false,20);
      string[] letters=["...","---","..."];
      foreach(string letter in letters)
      {
        for(int i=0;i<letter.Length;i++)
        {
          Add(true,letter[i]=='-'?18:6);
          if(i+1<letter.Length)Add(false,6);
        }
        Add(false,18);
      }
      Add(false,20);
      CwHsmmResult alternative=CwHsmmDecoder.Decode(
        frames.ToArray(),0.01,20,20);
      alternative.Text.Should().Be("SOS");
      alternative.EvidenceScore.Should().BeGreaterThan(0.93);
      var neural=new DeepCwDecodedText("5NN",[],100);
      CwHsmmDecoder.Arbitrate(neural,alternative)
        .Text.Should().Be("5NN");
      CwHsmmDecoder.Arbitrate(
        new DeepCwDecodedText("",[],100),alternative)
        .Text.Should().Be("SOS");
    }

    [Fact]
    public void Hsmm_DoesNotInventTextFromSteadyCarrier()
    {
      CwHsmmDecoder.Decode(
        Enumerable.Repeat(0.5f,600).ToArray(),0.01)
        .Text.Should().BeEmpty();
    }
  }
}
