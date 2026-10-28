using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class NativeRotatorFeedbackPolicyTests
{
  [Theory]
  [InlineData(1,800,false,false)]
  [InlineData(3,2400,false,false)]
  [InlineData(4,3999,false,false)]
  [InlineData(4,4000,false,true)]
  [InlineData(3,4500,true,false)]
  [InlineData(6,6999,true,false)]
  [InlineData(6,7000,true,false)]
  [InlineData(9,14999,true,false)]
  [InlineData(9,15000,true,true)]
  public void FeedbackSilenceSeparatesTransientRfiAndPersistentLoss(
    int misses,long ageMs,bool alreadySynchronized,bool reconnect)
  {
    Assert.Equal(reconnect,NativeRotatorFeedbackPolicy.ShouldReconnect(
      misses,ageMs,alreadySynchronized));
  }

  [Theory]
  [InlineData(1,600,1000,true,false)]
  [InlineData(2,1800,1000,true,false)]
  [InlineData(2,3000,1000,true,true)]
  [InlineData(2,2600,2000,true,false)]
  [InlineData(3,6000,2000,true,true)]
  [InlineData(8,10000,1000,false,false)]
  public void FeedbackLossStopsOnlyPotentiallyMovingControllersAfterGrace(
    int misses,long ageMs,int pollMs,bool moving,bool stop)
  {
    Assert.Equal(stop,NativeRotatorFeedbackPolicy.MustStopPossibleMotion(
      misses,ageMs,pollMs,moving));
  }
}
