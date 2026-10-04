using System;
using System.Collections.Generic;
using backs2.Core.Runner;

namespace backs2.Core.Analysis;

/// <summary>
/// Lightweight k-means with k-means++ seeding over intrinsic trade features.
/// Deterministic (fixed RNG seed) so the same trades produce the same
/// clusters on every run. Not thread-safe by design — one call, one array.
/// </summary>
public static class KMeansClusterer
{
    private const int Seed = 42;
    private const int FeatureCount = 4;

    /// <summary>
    /// Clusters <paramref name="trades"/> into <paramref name="k"/> groups.
    /// Returns one cluster id per trade, in the same order as the input list.
    /// </summary>
    public static int[] Cluster(IReadOnlyList<Trade> trades, int k, int maxIterations = 50)
    {
        int n = trades.Count;
        if (n == 0) return Array.Empty<int>();
        if (k < 1) k = 1;
        if (k > n) k = n;

        var features = BuildFeatures(trades);

        var rng = new Random(Seed);
        var centroids = new double[k][];
        for (int i = 0; i < k; i++) centroids[i] = new double[FeatureCount];

        InitPlusPlus(features, centroids, rng);

        var assignments = new int[n];
        Array.Fill(assignments, -1);
        var counts = new int[k];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;

            for (int i = 0; i < n; i++)
            {
                int best = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double d = SquaredDistance(features[i], centroids[c]);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
                if (assignments[i] != best)
                {
                    assignments[i] = best;
                    changed = true;
                }
            }

            if (!changed && iter > 0) break;

            for (int c = 0; c < k; c++)
            {
                Array.Clear(centroids[c], 0, FeatureCount);
                counts[c] = 0;
            }
            for (int i = 0; i < n; i++)
            {
                var f = features[i];
                var cen = centroids[assignments[i]];
                for (int d = 0; d < FeatureCount; d++) cen[d] += f[d];
                counts[assignments[i]]++;
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0) continue;
                for (int d = 0; d < FeatureCount; d++) centroids[c][d] /= counts[c];
            }
        }

        return assignments;
    }

    /// <summary>
    /// Feature vector: side (0=Short, 1=Long), PnL percent, duration minutes,
    /// entry hour-of-day. Standard-scaled so duration does not dominate the
    /// distance metric.
    /// </summary>
    private static double[][] BuildFeatures(IReadOnlyList<Trade> trades)
    {
        int n = trades.Count;
        var raw = new double[n][];

        for (int i = 0; i < n; i++)
        {
            var t = trades[i];
            double side = string.Equals(t.Side, "Long", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            double durationMin = Math.Max(0, (t.ExitTime - t.EntryTime).TotalMinutes);
            double hour = t.EntryTime.Hour;
            raw[i] = new[] { side, t.PnLPercent * 100.0, durationMin, hour };
        }

        var means = new double[FeatureCount];
        var stds = new double[FeatureCount];

        for (int d = 0; d < FeatureCount; d++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += raw[i][d];
            means[d] = sum / n;
        }

        for (int d = 0; d < FeatureCount; d++)
        {
            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                double diff = raw[i][d] - means[d];
                sumSq += diff * diff;
            }
            stds[d] = Math.Sqrt(sumSq / n);
            if (stds[d] < 1e-9) stds[d] = 1.0;
        }

        for (int i = 0; i < n; i++)
            for (int d = 0; d < FeatureCount; d++)
                raw[i][d] = (raw[i][d] - means[d]) / stds[d];

        return raw;
    }

    private static void InitPlusPlus(double[][] features, double[][] centroids, Random rng)
    {
        int n = features.Length;
        int k = centroids.Length;

        int firstIdx = rng.Next(n);
        Array.Copy(features[firstIdx], centroids[0], FeatureCount);

        var minDistSq = new double[n];
        for (int i = 0; i < n; i++)
            minDistSq[i] = SquaredDistance(features[i], centroids[0]);

        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < n; i++) total += minDistSq[i];

            int chosen;
            if (total <= 1e-12)
            {
                chosen = rng.Next(n);
            }
            else
            {
                double target = rng.NextDouble() * total;
                double acc = 0;
                chosen = 0;
                for (int i = 0; i < n; i++)
                {
                    acc += minDistSq[i];
                    if (acc >= target) { chosen = i; break; }
                }
            }

            Array.Copy(features[chosen], centroids[c], FeatureCount);

            for (int i = 0; i < n; i++)
            {
                double d = SquaredDistance(features[i], centroids[c]);
                if (d < minDistSq[i]) minDistSq[i] = d;
            }
        }
    }

    private static double SquaredDistance(double[] a, double[] b)
    {
        double sum = 0;
        for (int d = 0; d < FeatureCount; d++)
        {
            double diff = a[d] - b[d];
            sum += diff * diff;
        }
        return sum;
    }
}