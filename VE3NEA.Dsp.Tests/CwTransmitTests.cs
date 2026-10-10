using FluentAssertions;
using SkyRoof;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwTransmitTests
  {
    [Fact]
    public void StatusParser_RequiresKeySpeedAndSafetyFields()
    {
      CwKeyerStatus status =
        CwKeyerStatus.Parse(
          "STATUS IDLE MODE=CW BKIN=1 TX=0 KEYRAW=128 TXHZ=435000012");

      status.Lease.Should().Be("IDLE");
      status.Mode.Should().Be("CW");
      status.BreakIn.Should().Be(1);
      status.Transmitting.Should().BeFalse();
      status.KeySpeedRaw.Should().Be(128);
      status.ActualTxFrequencyHz.Should()
        .Be(435000012);
      status.ReadyToSend.Should().BeTrue();
      status.Wpm.Should()
        .BeApproximately(
          6 + 128 * 42.0 / 255.0,
          1e-9);

      Action missingKeySpeed =
        () => CwKeyerStatus.Parse(
          "STATUS IDLE MODE=CW BKIN=1 TX=0");

      missingKeySpeed.Should()
        .Throw<FormatException>();
    }

    [Fact]
    public void KeySpeedRaw_MapsDocumentedSixToFortyEightWpm()
    {
      CwMessageTiming.RawKeySpeedToWpm(0)
        .Should().Be(6);
      CwMessageTiming.RawKeySpeedToWpm(255)
        .Should().Be(48);
      CwMessageTiming.RawKeySpeedToWpm(128)
        .Should().BeApproximately(
          27.08235294117647,
          1e-9);
    }

    [Theory]
    [InlineData(6.0, 0)]
    [InlineData(20.0, 85)]
    [InlineData(48.0, 255)]
    public void Wpm_MapsToDocumentedRawControl(
      double wpm,
      int expectedRaw)
    {
      CwMessageTiming.WpmToRawKeySpeed(wpm)
        .Should().Be(expectedRaw);
    }

    [Fact]
    public void KeySpeedResult_ParsesVerifiedProtocolReply()
    {
      CwKeySpeedResult result =
        CwKeySpeedResult.Parse(
          "OK KEYRAW=85 WPM=20.00");
      result.KeySpeedRaw.Should().Be(85);
      result.Wpm.Should().Be(20.0);
    }

    [Fact]
    public void ProsignCaret_RemovesNormalInterCharacterGap()
    {
      TimeSpan ordinary =
        CwMessageTiming.EstimateDuration(
          "EE",
          20);
      TimeSpan joined =
        CwMessageTiming.EstimateDuration(
          "E^E",
          20);

      ordinary.TotalSeconds.Should()
        .BeApproximately(
          0.30,
          1e-9);
      joined.TotalSeconds.Should()
        .BeApproximately(
          0.18,
          1e-9);
      joined.Should().BeLessThan(ordinary);
    }

    [Fact]
    public void Watchdog_UsesConservativeFallbackAndBounds()
    {
      TimeSpan fallback =
        CwMessageTiming.ComputeWatchdog(
          "CQ DE BG5JSU",
          keySpeedRaw: null);
      TimeSpan fast =
        CwMessageTiming.ComputeWatchdog(
          "CQ DE BG5JSU",
          keySpeedRaw: 255);

      fallback.Should().BeGreaterThan(fast);
      fallback.Should()
        .BeLessThanOrEqualTo(
          TimeSpan.FromSeconds(210));
      fast.Should()
        .BeGreaterThanOrEqualTo(
          TimeSpan.FromSeconds(2));

      TimeSpan worstCase =
        CwMessageTiming.ComputeWatchdog(
          new string('0', 30),
          keySpeedRaw: 0);
      TimeSpan actualDuration =
        CwMessageTiming.EstimateDuration(
          new string('0', 30),
          6);

      worstCase.Should()
        .BeGreaterThan(actualDuration);
      worstCase.Should()
        .BeLessThanOrEqualTo(
          TimeSpan.FromSeconds(210));
    }

    [Fact]
    public async Task Controller_ManualSendDoesNotRequireLegacyEnableOrArm()
    {
      var settings = new CwConsoleSettings
      {
        TransmitEnabled = false, // legacy persisted setting is ignored
        CwKeyerPort = 4538
      };
      var factory = new FakeFactory();
      await using var controller =
        new CwTransmitController(settings, factory);

      // No Arm call, RX decoder, selected satellite, model, horizon or
      // transponder mode is needed for explicit operator CW keying.
      await controller.SendAsync("CQ");
      factory.ConnectCalls.Should().Be(1);
      factory.Session.SendCalls.Should().Be(1);
      controller.State.Sending.Should().BeTrue();
      await controller.StopAsync();
      controller.State.Sending.Should().BeFalse();
    }

    [Fact]
    public async Task Controller_SetKeySpeedRequiresVerifiedStatusReadback()
    {
      var factory = new FakeFactory();
      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory);

      CwKeyerStatus status =
        await controller.SetKeySpeedAsync(20.0);

      factory.Session.SetWpmCalls
        .Should().Be(1);
      status.KeySpeedRaw.Should().Be(85);
      controller.State.RadioStatus?.KeySpeedRaw
        .Should().Be(85);
    }

    [Fact]
    public async Task Controller_HoldsSessionUntilStop()
    {
      var settings =
        EnabledSettings();
      var factory =
        new FakeFactory();

      await using var controller =
        new CwTransmitController(
          settings,
          factory);

      controller.Arm();

      await controller.SendAsync(
        "CQ DE BG5JSU");

      controller.State.Sending
        .Should().BeTrue();
      controller.State.ActiveText
        .Should().Be("CQ DE BG5JSU");
      factory.Session.SendCalls
        .Should().Be(1);
      factory.Session.DisposeCalls
        .Should().Be(0);

      await controller.StopAsync();

      controller.State.Sending
        .Should().BeFalse();
      factory.Session.StopCalls
        .Should().Be(1);
      factory.Session.DisposeCalls
        .Should().Be(1);
    }

    [Theory]
    [InlineData("BUSY", "CW", 1, false)]
    [InlineData("IDLE", "USB", 1, false)]
    [InlineData("IDLE", "CW", 0, false)]
    [InlineData("IDLE", "CW", 1, true)]
    public async Task Controller_RejectsUnsafeStatusBeforeSend(
      string lease,
      string mode,
      int breakIn,
      bool tx)
    {
      var factory =
        new FakeFactory
        {
          Status =
            new(
              lease,
              mode,
              breakIn,
              tx,
              128)
        };

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory);

      controller.Arm();

      Func<Task> send =
        () => controller.SendAsync("CQ");

      await send.Should()
        .ThrowAsync<InvalidOperationException>();

      factory.Session.SendCalls
        .Should().Be(0);
      factory.Session.DisposeCalls
        .Should().Be(1);
    }

    [Fact]
    public async Task SatelliteInterlock_UsesSendHzAndFreezesTxCatWrites()
    {
      var factory =
        new FakeFactory
        {
          Status =
            new(
              "IDLE",
              "CW",
              1,
              false,
              128,
              435000025)
        };
      var interlock =
        new FakeInterlock(
          SatelliteSnapshot(
            expectedCatTxHz: 435000000,
            toleranceHz: 50));

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory,
          interlock);

      controller.Arm();
      await controller.SendAsync(
        "CQ");

      factory.Session.SendCalls
        .Should().Be(0);
      factory.Session.SendGuardedCalls
        .Should().Be(1);
      factory.Session.LastExpectedTxHz
        .Should().Be(435000000);
      factory.Session.LastToleranceHz
        .Should().Be(50);
      controller.TxCatWritesFrozen
        .Should().BeTrue();
      interlock.ValidateHardwareCalls
        .Should().Be(1);

      await controller.StopAsync();

      controller.TxCatWritesFrozen
        .Should().BeFalse();
    }

    [Fact]
    public async Task SatelliteInterlock_RejectsHardwareTxFrequencyBeforeSend()
    {
      var factory =
        new FakeFactory
        {
          Status =
            new(
              "IDLE",
              "CW",
              1,
              false,
              128,
              435000250)
        };
      var interlock =
        new FakeInterlock(
          SatelliteSnapshot(
            expectedCatTxHz: 435000000,
            toleranceHz: 50));

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory,
          interlock);

      controller.Arm();

      Func<Task> send =
        () => controller.SendAsync(
          "CQ");

      await send.Should()
        .ThrowAsync<InvalidOperationException>();

      factory.Session.SendCalls
        .Should().Be(0);
      factory.Session.SendGuardedCalls
        .Should().Be(0);
      controller.State.Sending
        .Should().BeFalse();
      controller.TxCatWritesFrozen
        .Should().BeFalse();
    }

    [Fact]
    public async Task SatelliteInterlockChange_StopsAndDisarmsActiveMessage()
    {
      var factory =
        new FakeFactory
        {
          Status =
            new(
              "IDLE",
              "CW",
              1,
              false,
              128,
              435000000)
        };
      var interlock =
        new FakeInterlock(
          SatelliteSnapshot(
            expectedCatTxHz: 435000000,
            toleranceHz: 50));

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory,
          interlock);

      controller.Arm();
      await controller.SendAsync(
        "CQ TEST");

      interlock.FailDuringSend =
        true;

      await WaitUntilAsync(
        () =>
          !controller.State.Sending &&
          !controller.State.Armed);

      factory.Session.StopCalls
        .Should().Be(1);
      controller.TxCatWritesFrozen
        .Should().BeFalse();
      controller.State.LastError
        .Should().Contain(
          "satellite TX interlock");
    }

    [Fact]
    public async Task StopFailure_StillDisposesLeaseConnection()
    {
      var factory =
        new FakeFactory();
      factory.Session.StopError =
        new IOException(
          "simulated STOP failure");

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory);

      controller.Arm();
      await controller.SendAsync("CQ");

      Func<Task> stop =
        () => controller.StopAsync();

      await stop.Should()
        .ThrowAsync<IOException>();

      controller.State.Sending
        .Should().BeFalse();
      factory.Session.StopCalls
        .Should().Be(1);
      factory.Session.DisposeCalls
        .Should().Be(1);
      controller.State.LastError
        .Should().Contain(
          "simulated STOP failure");
    }

    [Fact]
    public async Task LegacyTransmitDisabledSetting_DoesNotStopManualCwLease()
    {
      var factory = new FakeFactory();
      await using var controller =
        new CwTransmitController(EnabledSettings(), factory);

      await controller.SendAsync("CQ");
      controller.State.Sending.Should().BeTrue();

      await controller.ApplySettingsAsync(new CwConsoleSettings
      {
        TransmitEnabled = false,
        CwKeyerPort = 4538
      });

      // Changing a deprecated UI flag must not abort manual CW.
      // Explicit STOP, watchdog and connection teardown still apply.
      controller.State.Sending.Should().BeTrue();
      factory.Session.StopCalls.Should().Be(0);
      await controller.StopAsync();
      factory.Session.StopCalls.Should().Be(1);

      await controller.SendAsync("TEST");
      factory.Session.SendCalls.Should().Be(2);
      await controller.StopAsync();
    }

    [Fact]
    public async Task ChangingKeyerPort_StopsOldLeaseAndRequiresRearm()
    {
      var factory =
        new FakeFactory();

      await using var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory);

      controller.Arm();
      await controller.SendAsync("CQ");

      await controller.ApplySettingsAsync(
        new CwConsoleSettings
        {
          TransmitEnabled = true,
          CwKeyerPort = 4608
        });

      controller.State.Armed
        .Should().BeFalse();
      controller.State.Sending
        .Should().BeFalse();
      factory.Session.StopCalls
        .Should().Be(1);

      controller.Arm();
      await controller.SendAsync("TEST");

      factory.Ports.Should()
        .Equal(4538, 4608);

      await controller.StopAsync();
    }

    [Fact]
    public async Task Dispose_StopsActiveMessageAndDisarms()
    {
      var factory =
        new FakeFactory();
      var controller =
        new CwTransmitController(
          EnabledSettings(),
          factory);

      controller.Arm();
      await controller.SendAsync(
        "TEST");

      await controller.DisposeAsync();

      factory.Session.StopCalls
        .Should().Be(1);
      factory.Session.DisposeCalls
        .Should().Be(1);
      controller.State.Armed
        .Should().BeFalse();
    }

    private static CwTransmitInterlockSnapshot
      SatelliteSnapshot(
        long expectedCatTxHz,
        int toleranceHz) =>
      new(
        IsSatellite: true,
        SatelliteId: "SAT-1",
        TransmitterId: "TX-1",
        UplinkWithoutDopplerHz: 435000000,
        CorrectedUplinkHz: 435000000,
        ExpectedCatTxHz: expectedCatTxHz,
        CatLoOffsetHz: 0,
        FrequencyToleranceHz: toleranceHz,
        UplinkMode: Slicer.Mode.CW);

    private static async Task WaitUntilAsync(
      Func<bool> condition)
    {
      DateTime deadline =
        DateTime.UtcNow.AddSeconds(5);

      while (!condition() &&
             DateTime.UtcNow < deadline)
        await Task.Delay(20);

      condition().Should().BeTrue(
        "the CW safety monitor should react before timeout");
    }

    private static CwConsoleSettings
      EnabledSettings() =>
      new()
      {
        TransmitEnabled = true,
        CwKeyerPort = 4538
      };

    private sealed class FakeInterlock :
      ICwTransmitInterlock
    {
      private readonly CwTransmitInterlockSnapshot snapshot;

      public bool TxWritesFrozen { get; private set; }
      public bool FailDuringSend { get; set; }
      public int ValidateHardwareCalls { get; private set; }

      public FakeInterlock(
        CwTransmitInterlockSnapshot snapshot)
      {
        this.snapshot = snapshot;
      }

      public CwTransmitInterlockSnapshot
        CaptureForArm() =>
        snapshot;

      public CwTransmitInterlockSnapshot
        PrepareForSend(
          CwTransmitInterlockSnapshot armed) =>
        snapshot;

      public void ValidateDuringSend(
        CwTransmitInterlockSnapshot active)
      {
        if (FailDuringSend)
          throw new InvalidOperationException(
            "simulated satellite context change");
      }

      public void ValidateHardware(
        CwTransmitInterlockSnapshot active,
        CwKeyerStatus status)
      {
        ValidateHardwareCalls++;

        if (!active.RequiresHardwareFrequencyGuard)
          return;

        if (!status.ActualTxFrequencyHz.HasValue ||
            Math.Abs(
              status.ActualTxFrequencyHz.Value -
              active.ExpectedCatTxHz) >
            active.FrequencyToleranceHz)
          throw new InvalidOperationException(
            "simulated TX VFO mismatch");
      }

      public void SetTxWritesFrozen(
        bool frozen) =>
        TxWritesFrozen = frozen;
    }

    private sealed class FakeFactory :
      ICwKeyerSessionFactory
    {
      public FakeSession Session { get; } =
        new();

      public CwKeyerStatus Status {
        get => Session.Status;
        set => Session.Status = value;
      }

      public int ConnectCalls {
        get;
        private set;
      }

      public List<int> Ports { get; } = [];

      public Task<ICwKeyerSession>
        ConnectAsync(
          int port,
          CancellationToken cancellationToken = default)
      {
        ConnectCalls++;
        Ports.Add(port);
        return Task.FromResult<
          ICwKeyerSession>(Session);
      }
    }

    private sealed class FakeSession :
      ICwKeyerSession
    {
      public CwKeyerStatus Status {
        get;
        set;
      } = new(
        "IDLE",
        "CW",
        1,
        false,
        128);

      public int SetWpmCalls {
        get;
        private set;
      }

      public int SendCalls {
        get;
        private set;
      }

      public int SendGuardedCalls {
        get;
        private set;
      }

      public long LastExpectedTxHz {
        get;
        private set;
      }

      public int LastToleranceHz {
        get;
        private set;
      }

      public int StopCalls {
        get;
        private set;
      }

      public int DisposeCalls {
        get;
        private set;
      }

      public Exception? SendError {
        get;
        set;
      }

      public Exception? StopError {
        get;
        set;
      }

      public Task<CwKeyerStatus>
        GetStatusAsync(
          CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

      public Task<CwKeySpeedResult>
        SetWpmAsync(
          double wpm,
          CancellationToken cancellationToken = default)
      {
        SetWpmCalls++;
        int raw =
          CwMessageTiming.WpmToRawKeySpeed(wpm);
        Status = Status with
        {
          KeySpeedRaw = raw
        };
        return Task.FromResult(
          new CwKeySpeedResult(
            raw,
            CwMessageTiming.RawKeySpeedToWpm(raw)));
      }

      public Task SendAsync(
        string text,
        CancellationToken cancellationToken = default)
      {
        SendCalls++;

        if (SendError != null)
          return Task.FromException(
            SendError);

        return Task.CompletedTask;
      }

      public Task SendGuardedAsync(
        long expectedTxFrequencyHz,
        int toleranceHz,
        string text,
        CancellationToken cancellationToken = default)
      {
        SendGuardedCalls++;
        LastExpectedTxHz =
          expectedTxFrequencyHz;
        LastToleranceHz =
          toleranceHz;

        if (SendError != null)
          return Task.FromException(
            SendError);

        return Task.CompletedTask;
      }

      public Task StopAsync(
        CancellationToken cancellationToken = default)
      {
        StopCalls++;

        if (StopError != null)
          return Task.FromException(
            StopError);

        return Task.CompletedTask;
      }

      public ValueTask DisposeAsync()
      {
        DisposeCalls++;
        return ValueTask.CompletedTask;
      }
    }
  }
}
