// src/core/Runner/StrategyParameterParser.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace backs2.Core.Runner;

public static class StrategyParameterParser
{
    private const string Prefix = "// @param";

    public static IReadOnlyList<StrategyParameter> Parse(string source)
    {
        var result = new List<StrategyParameter>();
        if (string.IsNullOrEmpty(source)) return result;

        using var reader = new StringReader(source);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;

            if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            {
                if (!trimmed.StartsWith("//", StringComparison.Ordinal))
                    break;
                continue;
            }

            var parsed = ParseLine(trimmed.Substring(Prefix.Length).Trim());
            if (parsed != null) result.Add(parsed);
        }

        return result;
    }

    private static StrategyParameter? ParseLine(string body)
    {
        if (body.Length == 0) return null;

        var eqIdx = body.IndexOf('=');
        if (eqIdx <= 0) return null;

        var left = body.Substring(0, eqIdx)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (left.Length < 2) return null;

        var name = left[0];
        var type = left[1].ToLowerInvariant();

        var right = body.Substring(eqIdx + 1).Trim();
        var options = new List<string>();

        var optsIdx = right.IndexOf("options:", StringComparison.OrdinalIgnoreCase);
        if (optsIdx >= 0)
        {
            var optsPart = right.Substring(optsIdx + 8).Trim();
            right = right.Substring(0, optsIdx).Trim();

            foreach (var raw in optsPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = raw.Trim().Trim('"', '\'');
                if (v.Length > 0) options.Add(v);
            }
        }

        var defaultValue = right.Trim('"', '\'');
        return new StrategyParameter(name, type, defaultValue, options);
    }
}