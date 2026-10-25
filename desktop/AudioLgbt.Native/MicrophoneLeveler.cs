namespace AudioLgbt;

internal sealed class MicrophoneLeveler
{
    private double gain = 1;
    private double limiter = 1;
    private double speechEnergy;
    private double silenceSeconds;

    public void Process(Span<short> samples, int percent)
    {
        if (samples.IsEmpty) return;
        double energy = 0, peak = 0;
        foreach (var sample in samples)
        {
            double value = sample / 32768d;
            energy += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        double rms = Math.Sqrt(energy / samples.Length);
        double duration = samples.Length / 48000d;
        double previous = gain;
        if (rms >= .0032)
        {
            silenceSeconds = 0;
            double blend = 1 - Math.Exp(-duration / .25);
            speechEnergy += blend * (rms * rms - speechEnergy);
            double desired = Math.Clamp(.1 / Math.Sqrt(Math.Max(speechEnergy, rms * rms * .25)), .25, 8);
            double rate = desired < gain ? .08 : .35;
            gain += (1 - Math.Exp(-duration / rate)) * (desired - gain);
        }
        else
        {
            silenceSeconds += duration;
            if (silenceSeconds > .8)
            {
                gain += (1 - Math.Exp(-duration / .5)) * (1 - gain);
                speechEnergy *= Math.Exp(-duration / 2);
            }
        }
        double manual = Math.Clamp(percent / 100d, 0, 2);
        double ceiling = peak * Math.Max(previous, gain) * manual;
        double required = ceiling > .95 ? .95 / ceiling : 1;
        limiter = required < limiter ? required : Math.Min(required, limiter + (1 - Math.Exp(-duration / .15)) * (1 - limiter));
        for (int i = 0; i < samples.Length; i++)
        {
            double smoothGain = previous + (gain - previous) * (i + 1) / samples.Length;
            samples[i] = (short)Math.Clamp(Math.Round(samples[i] * smoothGain * manual * limiter), -31129, 31129);
        }
    }
}
