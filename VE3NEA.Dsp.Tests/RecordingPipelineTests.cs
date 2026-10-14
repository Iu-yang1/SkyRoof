using System;
using System.IO;
using MathNet.Numerics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SkyRoof;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RecordingPipelineTests
{
    [Fact]
    public void AppendSamplesHonorsEventCountNotPooledArrayCapacity()
    {
        float[] source = { 0.25f, -0.5f, 0.75f, 99f, 98f, 97f };
        float[]? destination = new float[2];
        int written = RecordingManager.AppendValidSamples(
            source, validCount: 3, ref destination, alreadyWritten: 0, maximum: 20);

        Assert.Equal(3, written);
        Assert.Equal(new[] { 0.25f, -0.5f, 0.75f },
            destination![..written]);
        Assert.DoesNotContain(99f, destination);
        Assert.True(destination.Length >= written);

        int next = RecordingManager.AppendValidSamples(
            new[] { 1f, 2f, 1000f }, validCount: 2,
            ref destination, alreadyWritten: written, maximum: 5);
        Assert.Equal(2, next);
        Assert.Equal(new[] { 0.25f, -0.5f, 0.75f, 1f, 2f },
            destination![..(written + next)]);
    }

    [Fact]
    public void AppendSamplesRespectsMaximumAndRejectsInvalidCount()
    {
        Complex32[]? destination = new Complex32[1];
        var source = new[]
        {
            new Complex32(1, 2), new Complex32(3, 4),
            new Complex32(5, 6), new Complex32(7, 8)
        };

        int added = RecordingManager.AppendValidSamples(
            source, 4, ref destination, 0, 3);
        Assert.Equal(3, added);
        Assert.Equal(3, destination!.Length);
        Assert.Equal(source[2], destination[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Complex32[]? copy = destination;
            RecordingManager.AppendValidSamples(source, 5, ref copy, 0, 10);
        });
    }

    [Fact]
    public void Pcm16StreamingProviderClipsAndStopsAtExactSampleCount()
    {
        var input = new float[] { -2f, -0.5f, 0.5f, 2f, 0.75f };
        var provider = new RecordingManager.RecordingPcm16WaveProvider(input, 4);
        var output = new byte[32];
        int first = provider.Read(output, 0, output.Length);

        Assert.Equal(8, first);
        Assert.Equal(48000, provider.WaveFormat.SampleRate);
        Assert.Equal(16, provider.WaveFormat.BitsPerSample);
        Assert.Equal(-32767, BitConverter.ToInt16(output, 0));
        Assert.Equal(32767, BitConverter.ToInt16(output, 6));
        Assert.Equal(0, provider.Read(output, 0, output.Length));
    }

    [Fact]
    public void NativeStereoCaptureCanBeMixedToMono()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var wave = new BufferedWaveProvider(format)
        {
            ReadFully = false
        };
        var stereo = new float[] { 1f, -1f, 0.5f, 0.25f };
        var bytes = new byte[stereo.Length * sizeof(float)];
        Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
        wave.AddSamples(bytes, 0, bytes.Length);
        var mono = new MonoMixSampleProvider(wave.ToSampleProvider());
        var output = new float[4];
        int count = mono.Read(output, 0, 2);

        Assert.Equal(2, count);
        Assert.Equal(0f, output[0], 4);
        Assert.Equal(0.375f, output[1], 4);
    }

    [Fact]
    public void RecorderWavWritesOnlyActualFramesWithoutPadding()
    {
        var recorder = new RecordingManager(new Context());
        typeof(RecordingManager).GetField("audioBuffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(recorder, new[] { 0.1f, 0.2f, 0.3f, 999f, 999f });
        typeof(RecordingManager).GetField("samplesInBuffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(recorder, 3);

        string filename = Path.Combine(Path.GetTempPath(),
            "skyroof-recorder-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            recorder.SaveRecording(filename, saveAsMp3: false);
            using var reader = new WaveFileReader(filename);
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(32, reader.WaveFormat.BitsPerSample);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(3 * sizeof(float), reader.Length);
        }
        finally
        {
            if (File.Exists(filename)) File.Delete(filename);
            if (File.Exists(filename + ".json")) File.Delete(filename + ".json");
        }
    }
}
