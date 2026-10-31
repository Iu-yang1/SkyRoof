using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using SkyRoof;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvPttReleaseTests
  {
    private static readonly DateTime Started =
      new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequiresFreshOffHardwareObservationAfterApplicationKeyed()
    {
      Assert.True(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));

      // OFF command acknowledgement alone is not a CAT RX-state read.
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddSeconds(-1),
        Started, Started.AddSeconds(1)));

      // Even a hardware OFF sample taken before *this* OFF request must
      // not be reused to clear the persistent fail-closed PTT alarm.
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddMilliseconds(500),
        Started.AddMilliseconds(600), Started.AddMilliseconds(700)));

      // A fresh readback of ON, or unresolved owned PTT, must fail closed.
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        true, false, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, true, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));
    }

    [Fact]
    public void LatePttOnAckMustNotEraseEmergencyStop()
    {
      // The CAT ON network call may return *after* the UI has queued OFF.
      // The completed ON must be recorded as ON and the queued OFF must
      // survive for the next CAT worker cycle. No radio is keyed by this test.
      MethodInfo method = typeof(CatControlEngine).GetMethod(
        "PendingAfterPttAck", BindingFlags.NonPublic | BindingFlags.Static)!;
      Assert.NotNull(method);
      bool? Complete(bool? latestRequest, bool commandActuallySent) =>
        (bool?)method.Invoke(null, new object?[] { latestRequest, commandActuallySent });

      Assert.Equal(false, Complete(false, true)); // STOP during in-flight ON
      Assert.Equal(true, Complete(true, false)); // ON during in-flight OFF
      Assert.Null(Complete(true, true));         // completed desired ON
      Assert.Null(Complete(false, false));       // completed desired OFF
      Assert.Null(Complete(null, true));         // no newer request
    }

    [Fact]
    public void EmergencyPttOffBypassesCtcssWriteFailure()
    {
      // CTCSS failures must never suppress UNKEY, but still inhibit KEY.
      MethodInfo method = typeof(CatControlEngine).GetMethod(
        "ShouldAttemptPtt", BindingFlags.NonPublic | BindingFlags.Static)!;
      Assert.NotNull(method);
      bool Run(bool toneReady, bool? pending) =>
        (bool)method.Invoke(null, new object?[] { toneReady, pending })!;
      Assert.True(Run(false, false));
      Assert.False(Run(false, true));
      Assert.True(Run(true, true));
      Assert.True(Run(true, false));
    }

    [Fact]
    public void CatHardwareReadbackTimestampsAreStateSpecific()
    {
      // Mock CAT observation fields without any network/radio I/O.
      var cat = (CatControlEngine)RuntimeHelpers.GetUninitializedObject(
        typeof(CatControlEngine));
      const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
      var offField = typeof(CatControlEngine).GetField(
        "lastPttOffReadbackTicks", flags)!;
      var onField = typeof(CatControlEngine).GetField(
        "lastPttOnReadbackTicks", flags)!;
      var offProperty = typeof(CatControlEngine).GetProperty(
        "LastPttOffHardwareReadbackUtc", flags)!;
      var onProperty = typeof(CatControlEngine).GetProperty(
        "LastPttOnHardwareReadbackUtc", flags)!;
      Assert.NotNull(offField);
      Assert.NotNull(onField);
      Assert.NotNull(offProperty);
      Assert.NotNull(onProperty);
      DateTime t = Started.AddSeconds(1);
      offField.SetValue(cat, t.Ticks);
      Assert.Equal(t, (DateTime)offProperty.GetValue(cat)!);
      Assert.Equal(DateTime.MinValue, (DateTime)onProperty.GetValue(cat)!);
      onField.SetValue(cat, t.AddSeconds(1).Ticks);
      Assert.Equal(t.AddSeconds(1), (DateTime)onProperty.GetValue(cat)!);
      Assert.Equal(t, (DateTime)offProperty.GetValue(cat)!);
    }

    [Fact]
    public void RejectsStaleFutureOrNonUtcReadback()
    {
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started,
        Started, Started.AddMilliseconds(1600)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddSeconds(3),
        Started, Started.AddMilliseconds(500)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false,
        DateTime.SpecifyKind(Started, DateTimeKind.Local),
        Started, Started.AddMilliseconds(500)));
    }
  }
}
