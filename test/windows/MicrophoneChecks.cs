using AudioLgbt;
using Concentus;
using Concentus.Enums;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class MicrophoneChecks
{
    private static short[] Voice(int frame, double amplitude)
    {
        var data = new short[960];
        for (int i = 0; i < data.Length; i++)
        {
            double t = (frame * 960 + i) / 48000d;
            double syllable = Math.Max(0, Math.Sin(2 * Math.PI * 3.1 * t));
            double phase = 2 * Math.PI * (145 * t + 1.7 * Math.Sin(2 * Math.PI * 1.3 * t));
            double voice = Math.Sin(phase) + .45 * Math.Sin(2 * phase) + .2 * Math.Sin(3 * phase);
            data[i] = (short)(Math.Clamp(amplitude * syllable * voice, -.99, .99) * 32767);
        }
        return data;
    }

    public static void Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        foreach (bool suppression in new[] { false, true })
        foreach (double amplitude in new[] { .025, .1, .7 })
        {
            var noise = new NoiseReducer(); var leveler = new MicrophoneLeveler();
            var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 32000;
            var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
            byte[] packet = new byte[4096]; short[] decoded = new short[1920];
            double rawEnergy = 0, filteredEnergy = 0, outputEnergy = 0, decodedEnergy = 0, peak = 0; long count = 0;
            for (int frame = 0; frame < 1500; frame++)
            {
                var pcm = Voice(frame, amplitude);
                if (frame >= 100) foreach (short s in pcm) rawEnergy += Math.Pow(s / 32768d, 2);
                if (suppression) noise.Process(pcm);
                if (frame >= 100) foreach (short s in pcm) filteredEnergy += Math.Pow(s / 32768d, 2);
                leveler.Process(pcm, 100);
                int length = encoder.Encode(pcm, 960, packet, packet.Length);
                int frames = decoder.Decode(packet.AsSpan(0, length), decoded, 960, false);
                if (frame < 100) continue;
                foreach (short s in pcm) { outputEnergy += Math.Pow(s / 32768d, 2); peak = Math.Max(peak, Math.Abs(s / 32768d)); count++; }
                foreach (short s in decoded.AsSpan(0, frames * 2)) decodedEnergy += Math.Pow(s / 32768d, 2) / 2;
            }
            double rms = Math.Sqrt(outputEnergy / count), decodedRms = Math.Sqrt(decodedEnergy / count);
            Console.WriteLine(JsonSerializer.Serialize(new { suppression, amplitude, inputRms = Math.Sqrt(rawEnergy / count), suppressionRatio = Math.Sqrt(filteredEnergy / rawEnergy), outputRms = rms, decodedRms, peak }));
            Check(rms >= .055 && rms <= .17, "Speech level is outside the useful range");
            Check(Math.Abs(20 * Math.Log10(decodedRms / rms)) < 2, "Opus/stereo path changes voice level");
            Check(peak <= .951, "Clipping after level control");
            Check(!suppression || Math.Sqrt(filteredEnergy / rawEnergy) > .8, "Noise suppression attenuates speech");
        }
        var a = new MicrophoneLeveler(); var b = new MicrophoneLeveler();
        for (int frame = 0; frame < 200; frame++)
        {
            var normal = Voice(frame, .02); var boost = (short[])normal.Clone();
            a.Process(normal, 100); b.Process(boost, 200);
            for (int i = 0; i < normal.Length; i++) Check(Math.Abs(boost[i] - normal[i] * 2) <= 1, "Manual gain is not applied after automatic leveling");
        }
        var transient = Enumerable.Repeat(short.MaxValue, 960).ToArray(); a.Process(transient, 200);
        Check(transient.All(s => s <= 31129), "Loud transient clips");
        var mute = Voice(1, .2); a.Process(mute, 0); Check(mute.All(s => s == 0), "Zero microphone level must mute");
        var quiet = new MicrophoneLeveler(); var random = new Random(7);
        for (int frame = 0; frame < 1000; frame++)
        {
            var floor = Enumerable.Range(0, 960).Select(_ => (short)random.Next(-32, 33)).ToArray();
            var original = (short[])floor.Clone(); quiet.Process(floor, 100);
            Check(floor.SequenceEqual(original), "Quiet background must not be amplified");
        }
        Console.WriteLine("PASS microphone normalization, Opus roundtrip, suppression, transient limiter, manual gain, mute and background floor");
    }

    public static async Task Capture()
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).First(d => d.FriendlyName == "CABLE Input (VB-Audio Virtual Cable)");
        int mic = Enumerable.Range(0, WaveIn.DeviceCount).First(i => WaveIn.GetCapabilities(i).ProductName.StartsWith("CABLE Output (VB-Audio"));
        var preferences = new Preferences { Microphone = mic, MicGain = 100, NoiseSuppression = true };
        using var capture = new AudioDevices(preferences);
        using var render = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 60);
        render.Init(new VoiceProvider());
        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        var pcm = new short[5760]; var gate = new object(); double energy = 0; long samples = 0;
        capture.Encoded += packet => { lock (gate) { int count = decoder.Decode(packet, pcm, pcm.Length, false); for (int i = 0; i < count; i++) energy += Math.Pow(pcm[i] / 32768d, 2); samples += count; } };
        render.Play(); capture.SetMuted(false); await Task.Delay(2500);
        lock (gate) { energy = 0; samples = 0; }
        await Task.Delay(3500);
        double rms; long total; lock (gate) { rms = Math.Sqrt(energy / Math.Max(1, samples)); total = samples; }
        if (total < 48000 || rms < .05 || rms > .17) throw new Exception($"Actual microphone capture level: {rms}, samples: {total}");
        preferences.MicGain = 0; await Task.Delay(250); lock (gate) { energy = 0; samples = 0; }
        await Task.Delay(350); lock (gate) { if (energy / Math.Max(1, samples) > .000001) throw new Exception("Live microphone level 0 failed"); }
        capture.SetMuted(true); render.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { capture = "WASAPI virtual cable to WaveIn, noise suppression, leveler, Opus", inputRms = .00984, decodedRms = rms, samples = total, liveMute = true }));
    }

    private sealed class VoiceProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        private int frame, offset; private byte[] current = [];
        public int Read(byte[] buffer, int start, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (offset == current.Length) { var pcm = Voice(frame++, .025); current = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, current, 0, current.Length); offset = 0; }
                int take = Math.Min(count - written, current.Length - offset);
                Array.Copy(current, offset, buffer, start + written, take); written += take; offset += take;
            }
            return written;
        }
    }
}
