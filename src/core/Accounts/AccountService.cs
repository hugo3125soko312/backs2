using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace backs2.Core.Accounts;

public sealed record AccountDescriptor(
    string FilePath,
    string FileStem,
    string Firm,
    string Plan,
    string Size,
    double StartingCapital
);

public sealed record ActiveAccountSelection(
    string FirmFileStem,
    string Firm,
    string Plan,
    string Size
);

public sealed class AccountService
{
    private const string AccountsFolder = "data/accounts";
    private const string Extension = ".account.json";

    private readonly ConfigManager _cfg;
    private readonly LogManager _log;
    private readonly object _gate = new();

    private List<AccountDescriptor> _descriptors = new();
    private readonly Dictionary<string, FirmConfig> _firmCache = new(StringComparer.OrdinalIgnoreCase);
    private AccountDescriptor? _active;

    public AccountService(ConfigManager cfg, LogManager log)
    {
        _cfg = cfg;
        _log = log;

        Directory.CreateDirectory(AccountsFolder);
        Refresh();
        TryLoadActiveFromSettings();
    }

    public IReadOnlyList<AccountDescriptor> Descriptors
    {
        get { lock (_gate) { return _descriptors; } }
    }

    public AccountDescriptor? Active
    {
        get { lock (_gate) { return _active; } }
    }

    public event EventHandler<AccountDescriptor?>? ActiveChanged;

    public void Refresh()
    {
        _firmCache.Clear();

        List<AccountDescriptor> snapshot;
        try
        {
            snapshot = Directory
                .EnumerateFiles(AccountsFolder, "*" + Extension, SearchOption.TopDirectoryOnly)
                .SelectMany(ExpandFirmFile)
                .OrderBy(d => d.Firm, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Plan, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Size, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Account scan failed: {ex.Message}");
            snapshot = new List<AccountDescriptor>();
        }

        lock (_gate) { _descriptors = snapshot; }
    }

    private IEnumerable<AccountDescriptor> ExpandFirmFile(string path)
    {
        var firm = LoadFirm(path);
        if (firm == null) yield break;

        var stem = Path.GetFileName(path)[..^Extension.Length];

        foreach (var plan in firm.Plans)
        {
            foreach (var size in plan.Sizes)
            {
                yield return new AccountDescriptor(
                    FilePath: path,
                    FileStem: stem,
                    Firm: firm.Firm,
                    Plan: plan.Plan,
                    Size: size.Size,
                    StartingCapital: size.StartingCapital);
            }
        }
    }

    public FirmConfig? LoadFirm(string filePath)
    {
        lock (_gate)
        {
            if (_firmCache.TryGetValue(filePath, out var cached)) return cached;
        }

        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var parsed = JsonSerializer.Deserialize(json, AccountJsonContext.Default.FirmConfig);
            if (parsed != null)
            {
                lock (_gate) { _firmCache[filePath] = parsed; }
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _log.Error($"Firm load failed: {Path.GetFileName(filePath)} - {ex.Message}");
            return null;
        }
    }

    public PlanSize? ResolvePlanSize(AccountDescriptor descriptor)
    {
        var firm = LoadFirm(descriptor.FilePath);
        if (firm == null) return null;

        var plan = firm.Plans.FirstOrDefault(p =>
            string.Equals(p.Plan, descriptor.Plan, StringComparison.OrdinalIgnoreCase));
        if (plan == null) return null;

        return plan.Sizes.FirstOrDefault(s =>
            string.Equals(s.Size, descriptor.Size, StringComparison.OrdinalIgnoreCase));
    }

    public void SetActive(AccountDescriptor descriptor)
    {
        var size = ResolvePlanSize(descriptor);
        if (size == null)
        {
            _log.Error($"SetActive failed: could not resolve {descriptor.Firm} {descriptor.Plan} {descriptor.Size}");
            return;
        }

        lock (_gate) { _active = descriptor; }

        _cfg.Settings.ActiveAccountFirmFile = descriptor.FileStem;
        _cfg.Settings.ActiveAccountPlan = descriptor.Plan;
        _cfg.Settings.ActiveAccountSize = descriptor.Size;
        _cfg.SaveConfig();

        _log.Info($"Account active: {descriptor.Firm} · {descriptor.Plan} · {descriptor.Size}");
        ActiveChanged?.Invoke(this, descriptor);
    }

    private void TryLoadActiveFromSettings()
    {
        var stem = _cfg.Settings.ActiveAccountFirmFile;
        var plan = _cfg.Settings.ActiveAccountPlan;
        var size = _cfg.Settings.ActiveAccountSize;

        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(plan) || string.IsNullOrWhiteSpace(size))
            return;

        AccountDescriptor? match;
        lock (_gate)
        {
            match = _descriptors.FirstOrDefault(d =>
                string.Equals(d.FileStem, stem, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Plan, plan, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Size, size, StringComparison.OrdinalIgnoreCase));
        }

        if (match == null)
        {
            _log.Info($"Active account '{stem} / {plan} / {size}' referenced in settings but not found on disk.");
            return;
        }

        lock (_gate) { _active = match; }
    }
}