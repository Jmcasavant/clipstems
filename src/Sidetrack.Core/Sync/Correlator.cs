namespace Sidetrack.Core.Sync;

/// <summary>Where the probe sits inside the reference, and how sure that is.</summary>
/// <param name="LagSamples">Offset of the probe's first sample in the reference, sub-sample refined.</param>
/// <param name="Peak">Normalized correlation at the best lag, 0..1.</param>
/// <param name="RunnerUp">Best normalized correlation more than the exclusion zone away from the peak.</param>
public sealed record CorrelationResult(double LagSamples, double Peak, double RunnerUp)
{
    public double Margin => RunnerUp <= 1e-9 ? double.PositiveInfinity : Peak / RunnerUp;
}

/// <summary>
/// Finds a short probe (Steam's clip audio) inside a longer reference (what
/// Sidetrack recorded around the clip) by FFT cross-correlation, normalized
/// per lag so loud passages do not win by volume alone.
/// </summary>
public static class Correlator
{
    /// <summary>Correlation needed to call sync verified.</summary>
    public const double MinPeak = 0.25;

    /// <summary>How much the peak must beat the next-best lag.</summary>
    public const double MinMargin = 1.5;

    public static bool IsConfident(CorrelationResult r) => r.Peak >= MinPeak && r.Margin >= MinMargin;

    /// <summary>
    /// Returns null when either signal has no energy to correlate (silence),
    /// which callers must treat as "sync unverified", never as offset zero.
    /// </summary>
    public static CorrelationResult? Locate(float[] reference, float[] probe, int exclusionSamples)
    {
        if (probe.Length == 0 || reference.Length < probe.Length) return null;
        var r = Whiten(reference);
        var p = Whiten(probe);

        double probeNorm = Math.Sqrt(p.Sum(x => x * x));
        if (probeNorm < 1e-6) return null;

        int lags = r.Length - p.Length + 1;
        int n = Fft.NextPowerOfTwo(r.Length + p.Length);
        var aRe = new double[n]; var aIm = new double[n];
        var bRe = new double[n]; var bIm = new double[n];
        Array.Copy(r, aRe, r.Length);
        Array.Copy(p, bRe, p.Length);
        Fft.Transform(aRe, aIm, inverse: false);
        Fft.Transform(bRe, bIm, inverse: false);
        for (int i = 0; i < n; i++)
        {
            // reference × conj(probe) → cross-correlation at non-negative lags
            double re = aRe[i] * bRe[i] + aIm[i] * bIm[i];
            double im = aIm[i] * bRe[i] - aRe[i] * bIm[i];
            aRe[i] = re;
            aIm[i] = im;
        }
        Fft.Transform(aRe, aIm, inverse: true);

        // Energy of each reference window via prefix sums.
        var prefix = new double[r.Length + 1];
        for (int i = 0; i < r.Length; i++) prefix[i + 1] = prefix[i] + r[i] * r[i];

        var score = new double[lags];
        int best = -1;
        for (int lag = 0; lag < lags; lag++)
        {
            double energy = prefix[lag + p.Length] - prefix[lag];
            score[lag] = energy < 1e-9 ? 0 : aRe[lag] / (probeNorm * Math.Sqrt(energy));
            if (best < 0 || score[lag] > score[best]) best = lag;
        }
        if (best < 0 || score[best] <= 0) return null;

        double runnerUp = 0;
        for (int lag = 0; lag < lags; lag++)
            if (Math.Abs(lag - best) > exclusionSamples) runnerUp = Math.Max(runnerUp, score[lag]);

        return new CorrelationResult(best + ParabolicOffset(score, best), score[best], runnerUp);
    }

    /// <summary>
    /// Removes DC and takes the first difference, which flattens the spectrum
    /// enough that bass does not smear the peak across many lags.
    /// </summary>
    private static double[] Whiten(float[] x)
    {
        if (x.Length == 0) return [];
        double mean = 0;
        foreach (var v in x) mean += v;
        mean /= x.Length;
        var y = new double[x.Length];
        double prev = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i] - mean;
            y[i] = v - prev;
            prev = v;
        }
        y[0] = 0;
        return y;
    }

    private static double ParabolicOffset(double[] s, int i)
    {
        if (i <= 0 || i >= s.Length - 1) return 0;
        double denom = s[i - 1] - 2 * s[i] + s[i + 1];
        return Math.Abs(denom) < 1e-12 ? 0 : Math.Clamp(0.5 * (s[i - 1] - s[i + 1]) / denom, -0.5, 0.5);
    }
}
