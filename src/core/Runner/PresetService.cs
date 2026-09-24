// src/core/Runner/PresetService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace backs2.Core.Runner;

public sealed record PresetDescriptor(
    string FilePath,
    string StrategyName,
    string PresetName,
    DateTime LastWriteUtc,
    long SizeBytes
);

public sealed record Preset(
    string StrategyName,
    string PresetName,
    DateTime CreatedUtc,
    Dictionary<string, string> Values
);

public sealed class PresetService
{
    private const string Extension = ".preset.json";

    private readonly string _root;
    private readonly object _gate = new();

    public PresetService(ConfigManager cfg)
    {
        _root = Path.Combine(cfg.ConfigDirectory, "presets");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public IReadOnlyList<PresetDescriptor> ListForStrategy(string strategyName)
    {
        if (string.IsNullOrWhiteSpace(strategyName))
            return Array.Empty<PresetDescriptor>();

        return ListAll()
            .Where(p => string.Equals(p.StrategyName, strategyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<PresetDescriptor> ListAll()
    {
        lock (_gate)
        {
            try
            {
                return Directory
                    .EnumerateFiles(_root, "*" + Extension, SearchOption.TopDirectoryOnly)
                    .Select(BuildDescriptor)
                    .Where(d => d != null)
                    .Cast<PresetDescriptor>()
                    .OrderBy(p => p.StrategyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.PresetName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return Array.Empty<PresetDescriptor>();
            }
        }
    }

    public Preset? Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        lock (_gate)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<Preset>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(string strategyName, string presetName, Dictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(strategyName))
            throw new ArgumentException("Strategy name is required.", nameof(strategyName));

        if (string.IsNullOrWhiteSpace(presetName))
            throw new ArgumentException("Preset name is required.", nameof(presetName));

        var safeStrategy = Sanitize(strategyName);
        var safePreset = Sanitize(presetName);
        var file = Path.Combine(_root, $"{safeStrategy}_{safePreset}{Extension}");

        var payload = new Preset(
            strategyName,
            presetName,
            DateTime.UtcNow,
            values ?? new Dictionary<string, string>());

        var json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true });

        lock (_gate)
        {
            File.WriteAllText(file, json);
        }
    }

    public void Delete(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        lock (_gate)
        {
            try { File.Delete(filePath); } catch { }
        }
    }

    public string GetNextSequentialName(string strategyName)
    {
        if (string.IsNullOrWhiteSpace(strategyName)) return "001";

        var existing = ListForStrategy(strategyName);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in existing) taken.Add(p.PresetName);

        int n = 1;
        while (taken.Contains($"{strategyName}_{n:D3}")) n++;
        return $"{strategyName}_{n:D3}";
    }

    private PresetDescriptor? BuildDescriptor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var stem = info.Name;

            if (stem.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - Extension.Length);

            var idx = stem.IndexOf('_');
            var strat = idx > 0 ? stem.Substring(0, idx) : stem;
            var preset = idx > 0 ? stem.Substring(idx + 1) : stem;

            return new PresetDescriptor(
                info.FullName,
                strat,
                preset,
                info.LastWriteTimeUtc,
                info.Length);
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] == '_')
                chars[i] = '-';
        }
        return new string(chars);
    }
}