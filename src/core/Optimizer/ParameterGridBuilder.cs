using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace backs2.Core.Optimizer;

/// <summary>
/// One parameter's search specification, supplied by the Runner's UI.
/// Numeric parameters with Min/Max/Increment set produce a range of values;
/// bool produces {true, false}; string with Options enumerates them; anything
/// else falls back to FixedValue (single-value search space).
/// </summary>
public sealed record GridParameter(
    string Name,
    string Type,
    string FixedValue,
    double? Min,
    double? Max,
    double? Increment,
    IReadOnlyList<string> Options);

/// <summary>
/// Result of a grid build. When WasSampled is true, Combinations is a
/// random sample of TotalGenerated rather than the full grid — TotalGenerated
/// may be larger than Combinations.Count.
/// </summary>
public sealed record GridBuildResult(
    IReadOnlyList<ParameterSet[]> Combinations,
    int TotalGenerated,
    bool WasSampled,
    bool WasCapped);

/// <summary>
/// Generates the parameter search space for one optimization run. Full
/// Cartesian product when the total fits under the cap; deterministic random
/// sample (seed 42) when it does not. The seed keeps successive runs of the
/// same grid reproducible.
/// </summary>
public static class ParameterGridBuilder
{
    private const int SampleSeed = 42;
    private const int PerParameterValueGuard = 10_000;

    public static GridBuildResult Build(
        IReadOnlyList<GridParameter> parameters,
        int maxCombinations)
    {
        if (parameters == null || parameters.Count == 0)
            throw new ArgumentException("At least one parameter is required.", nameof(parameters));
        if (maxCombinations < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCombinations));

        // Per-parameter value lists
        var valueLists = new List<List<string>>(parameters.Count);
        long total = 1;
        foreach (var p in parameters)
        {
            var values = GetValuesFor(p);
            valueLists.Add(values);
            total *= values.Count;
            if (total > int.MaxValue) total = int.MaxValue; // overflow guard
        }

        if (total <= maxCombinations)
        {
            var all = CartesianProduct(parameters, valueLists);
            return new GridBuildResult(all, all.Count, false, false);
        }

        // Over the cap — random sample instead of full product.
        var sampled = RandomSample(parameters, valueLists, maxCombinations, (int)Math.Min(total, int.MaxValue));
        return new GridBuildResult(sampled, (int)Math.Min(total, int.MaxValue), true, true);
    }

    private static List<string> GetValuesFor(GridParameter p)
    {
        if (string.Equals(p.Type, "bool", StringComparison.OrdinalIgnoreCase))
            return new List<string> { "true", "false" };

        bool numeric = string.Equals(p.Type, "int", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Type, "double", StringComparison.OrdinalIgnoreCase);

        if (numeric && p.Min.HasValue && p.Max.HasValue && p.Increment.HasValue && p.Increment.Value > 0)
        {
            var values = new List<string>();
            double v = p.Min.Value;
            double max = p.Max.Value;
            double inc = p.Increment.Value;
            int guard = 0;

            while (v <= max + 1e-9 && guard < PerParameterValueGuard)
            {
                if (string.Equals(p.Type, "int", StringComparison.OrdinalIgnoreCase))
                    values.Add(((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture));
                else
                    values.Add(v.ToString("0.######", CultureInfo.InvariantCulture));
                v += inc;
                guard++;
            }

            if (values.Count == 0) values.Add(p.FixedValue);
            return values;
        }

        if (string.Equals(p.Type, "string", StringComparison.OrdinalIgnoreCase) && p.Options.Count > 0)
            return new List<string>(p.Options);

        return new List<string> { p.FixedValue };
    }

    private static List<ParameterSet[]> CartesianProduct(
        IReadOnlyList<GridParameter> parameters,
        List<List<string>> valueLists)
    {
        int n = parameters.Count;
        var result = new List<ParameterSet[]>();

        if (n == 0) return result;

        int total = 1;
        for (int i = 0; i < n; i++) total *= valueLists[i].Count;
        result.Capacity = total;

        var indices = new int[n];
        for (int iter = 0; iter < total; iter++)
        {
            var combo = new ParameterSet[n];
            for (int i = 0; i < n; i++)
            {
                combo[i] = new ParameterSet(
                    parameters[i].Name,
                    parameters[i].Type,
                    valueLists[i][indices[i]]);
            }
            result.Add(combo);

            // Odometer increment — least-significant on the right
            for (int i = n - 1; i >= 0; i--)
            {
                indices[i]++;
                if (indices[i] < valueLists[i].Count) break;
                indices[i] = 0;
            }
        }

        return result;
    }

    private static List<ParameterSet[]> RandomSample(
        IReadOnlyList<GridParameter> parameters,
        List<List<string>> valueLists,
        int sampleSize,
        int totalGenerated)
    {
        int n = parameters.Count;
        var rng = new Random(SampleSeed);
        var result = new List<ParameterSet[]>(sampleSize);
        var seen = new HashSet<string>();

        // Hard stop to prevent an infinite loop if the distinct value space
        // is smaller than the sample size (rare — happens when per-parameter
        // value lists have lots of duplicates).
        int maxAttempts = sampleSize * 50;

        for (int attempt = 0; attempt < maxAttempts && result.Count < sampleSize; attempt++)
        {
            var combo = new ParameterSet[n];
            var sb = new StringBuilder(n * 12);

            for (int i = 0; i < n; i++)
            {
                var value = valueLists[i][rng.Next(valueLists[i].Count)];
                combo[i] = new ParameterSet(parameters[i].Name, parameters[i].Type, value);
                sb.Append(value).Append('|');
            }

            var key = sb.ToString();
            if (seen.Add(key))
                result.Add(combo);

            if (seen.Count >= totalGenerated) break;
        }

        return result;
    }
}