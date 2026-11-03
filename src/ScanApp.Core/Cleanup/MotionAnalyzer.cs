using ScanApp.Core.Storage;

namespace ScanApp.Core.Cleanup;

/// <summary>
/// Finds the still phases, pauses and backward slips of a hand-pushed pass from the profiles alone (there is no encoder).
/// The motion signal is the mean |Z(i) - Z(i - Lag)|: near the sensor noise when still, much larger when moving.
/// </summary>
public static class MotionAnalyzer
{
    private const int PointStride = 8;          // backward search compares every 8th point
    private const int CandidateStep = 10;       // ...against every 10th earlier profile
    private const int CandidateWindow = 2000;   // ...over the last 2 000 profiles
    private const int MergeGap = 4 * CandidateStep;
    private const int MinComparedPoints = 8;

    public static MotionAnalysis Analyze(PassReader reader, MotionOptions options,
                                         IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Lag, 1);
        int n = reader.ProfileCount;
        int lag = options.Lag;
        var timeS = new double[n];
        var lagDiff = new float[n];       // mean |Z(i) - Z(i - lag)|, NaN where undefined
        var bestDist = new float[n];      // best earlier-profile match, NaN where none was searched
        var bestFrom = new int[n];
        Array.Fill(lagDiff, float.NaN);
        Array.Fill(bestDist, float.NaN);

        var zRing = new float[lag + 1][];
        var candidates = new float[CandidateWindow / CandidateStep + 2][];
        long firstNs = 0;
        int reportEvery = Math.Max(1, n / 100), index = 0;

        foreach (var p in reader.ReadAll())
        {
            ct.ThrowIfCancellationRequested();
            if (index == 0) firstNs = p.TimestampNs;
            timeS[index] = (p.TimestampNs - firstNs) / 1e9;
            var z = p.Z;

            if (index >= lag)
                lagDiff[index] = MeanAbsDiff(z, zRing[(index - lag) % zRing.Length]);

            var small = Downsample(z);
            // Searching is only worth it where the pass is moving; StillThreshold is never below MinStillMm.
            if (index > 2 * lag && lagDiff[index] > options.MinStillMm)
                SearchEarlier(small, index, lag, candidates, out bestDist[index], out bestFrom[index]);

            zRing[index % zRing.Length] = z;
            if (index % CandidateStep == 0)
                candidates[index / CandidateStep % candidates.Length] = small;

            index++;
            if (progress is not null && (index % reportEvery == 0 || index == n))
                progress.Report((double)index / n);
        }

        return Interpret(options, n, timeS, lagDiff, bestDist, bestFrom);
    }

    private static float[] Downsample(float[] z)
    {
        var small = new float[(z.Length + PointStride - 1) / PointStride];
        for (int k = 0; k < small.Length; k++) small[k] = z[k * PointStride];
        return small;
    }

    private static void SearchEarlier(float[] small, int index, int lag, float[][] candidates,
                                      out float best, out int bestFrom)
    {
        best = float.NaN;
        bestFrom = -1;
        int oldest = Math.Max(0, index - CandidateWindow);
        int first = (oldest + CandidateStep - 1) / CandidateStep * CandidateStep;
        for (int j = first; j < index - 2 * lag; j += CandidateStep)
        {
            float d = MeanAbsDiff(small, candidates[j / CandidateStep % candidates.Length]);
            if (!float.IsNaN(d) && (float.IsNaN(best) || d < best))
            {
                best = d;
                bestFrom = j;
            }
        }
    }

    /// <summary>Mean |a - b| over points valid in both, NaN with too few.</summary>
    private static float MeanAbsDiff(float[] a, float[] b)
    {
        double sum = 0;
        int count = 0;
        for (int k = 0; k < a.Length; k++)
        {
            float d = a[k] - b[k];
            if (float.IsNaN(d)) continue;
            sum += Math.Abs(d);
            count++;
        }
        return count >= MinComparedPoints ? (float)(sum / count) : float.NaN;
    }

    private static MotionAnalysis Interpret(MotionOptions o, int n, double[] timeS, float[] lagDiff,
                                            float[] bestDist, int[] bestFrom)
    {
        var whole = new ProfileRange(0, n - 1);
        var signal = new float[n];
        Array.Fill(signal, float.NaN);
        if (n <= o.Lag)
            return new MotionAnalysis(signal, timeS, float.NaN, o.MinStillMm, false, whole, false, [], []);

        // Centre the lag difference on the middle of the window it measures, then smooth with a moving median.
        int half = o.Lag / 2;
        var centred = new float[n];
        Array.Fill(centred, float.NaN);
        for (int i = o.Lag; i < n; i++) centred[i - half] = lagDiff[i];

        double rate = n > 1 && timeS[n - 1] > 0 ? (n - 1) / timeS[n - 1] : 1000;
        int window = Math.Max(1, (int)Math.Round(o.SmoothingS * rate)) | 1;
        MovingMedian(centred, signal, window);

        var valid = signal.Where(v => !float.IsNaN(v)).Order().ToArray();
        float floor = valid.Length == 0 ? float.NaN
            : valid[Math.Clamp((int)(o.FloorPercentile * valid.Length), 0, valid.Length - 1)];
        // A pass with little still time has no clean reference; cap the floor near the real sensor noise.
        bool capped = floor > o.MaxNoiseFloorMm;
        if (capped) floor = o.MaxNoiseFloorMm;
        float threshold = float.IsNaN(floor) ? o.MinStillMm : Math.Max(o.MinStillMm, (float)(o.FloorFactor * floor));

        // A window straddling a moving edge reads as moving, so move each edge inwards by half a lag.
        int minMoving = (int)Math.Ceiling(o.MinMovingS * rate);
        var moving = new List<ProfileRange>();
        foreach (var run in Runs(n, k => signal[k] > threshold))
        {
            if (run.End - run.Start + 1 < minMoving) continue;
            int start = run.Start + half, end = run.End - half;
            moving.Add(start <= end ? new ProfileRange(start, end) : run);
        }

        var pauses = new List<ProfileRange>();
        int minPause = (int)Math.Ceiling(o.MinPauseS * rate);
        for (int r = 1; r < moving.Count; r++)
        {
            var gap = new ProfileRange(moving[r - 1].End + 1, moving[r].Start - 1);
            if (gap.End - gap.Start + 1 >= minPause) pauses.Add(gap);
        }

        var backward = BackwardRuns(o, n, timeS, lagDiff, bestDist, bestFrom, threshold);
        var trim = moving.Count == 0 ? whole : new ProfileRange(moving[0].Start, moving[^1].End);
        return new MotionAnalysis(signal, timeS, floor, threshold, capped, trim, moving.Count > 0, pauses, backward);
    }

    private static List<ProfileRange> BackwardRuns(MotionOptions o, int n, double[] timeS, float[] lagDiff,
                                                   float[] bestDist, int[] bestFrom, float threshold)
    {
        var runs = new List<ProfileRange>();
        var hits = new List<int>();
        for (int i = 0; i < n; i++)
            if (bestDist[i] <= threshold && lagDiff[i] > threshold) hits.Add(i);

        for (int s = 0; s < hits.Count;)
        {
            int e = s;
            while (e + 1 < hits.Count && hits[e + 1] - hits[e] <= MergeGap) e++;
            int start = hits[s], end = hits[e];
            // Moving backward, the matched earlier profile recedes; a repeating pattern keeps a constant age instead.
            int receding = 0, advancing = 0;
            for (int k = s; k < e; k++)
            {
                int d = bestFrom[hits[k + 1]] - bestFrom[hits[k]];
                if (d < 0) receding++;
                else if (d > 0) advancing++;
            }
            if (timeS[end] - timeS[start] >= o.MinBackwardS && receding > advancing)
                runs.Add(new ProfileRange(start, end));
            s = e + 1;
        }
        return runs;
    }

    /// <summary>Maximal runs of indices where <paramref name="test"/> holds.</summary>
    private static IEnumerable<ProfileRange> Runs(int n, Func<int, bool> test)
    {
        int start = -1;
        for (int k = 0; k < n; k++)
        {
            bool on = test(k);
            if (on && start < 0) start = k;
            else if (!on && start >= 0)
            {
                yield return new ProfileRange(start, k - 1);
                start = -1;
            }
        }
        if (start >= 0) yield return new ProfileRange(start, n - 1);
    }

    private static void MovingMedian(float[] input, float[] output, int window)
    {
        int half = window / 2;
        var buffer = new float[window];
        for (int i = 0; i < input.Length; i++)
        {
            int count = 0;
            for (int k = Math.Max(0, i - half); k <= Math.Min(input.Length - 1, i + half); k++)
                if (!float.IsNaN(input[k])) buffer[count++] = input[k];
            if (count == 0) continue;
            Array.Sort(buffer, 0, count);
            output[i] = buffer[count / 2];
        }
    }
}
