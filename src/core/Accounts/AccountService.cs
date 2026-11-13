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

/// <summary>
/// Resolved active-account triple with its concrete funded phase. Produced
/// by AccountService.ResolveActiveFundedContext(). Consumers (Monte Carlo,
/// the parameter optimizer, the Runner) all need the same three things:
/// the descriptor, the size (for StartingCapital), and the funded phase
/// (the last entry in Phases — that is where prop-firm rule evaluation
/// lives). Keeping the extraction in one place means a change to that
/// convention only has to happen once.
/// </summary>
public sealed record ActiveFundedContext(
    AccountDescriptor Account,
    PlanSize PlanSize,
    AccountPhase FundedPhase
);

public sealed class AccountService
{
    private const string AccountsFolder = "data/accounts";
    private const string Extension = ".account.json";

    private readonly ConfigManager _cfg;
    private readonly object _gate = new();

    private List<AccountDescriptor> _descriptors = new();
    private readonly Dictionary<string, FirmConfig> _firmCache = new(StringComparer.OrdinalIgnoreCase);
    private AccountDescriptor? _active;

    public string RootDirectory => AccountsFolder;

    public AccountService(ConfigManager cfg)
    {
        _cfg = cfg;

        Directory.CreateDirectory(AccountsFolder);
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Refresh();
                TryLoadActiveFromSettings();
                IsInitialized = true;
                Initialized?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Account, "account async init failed", ex);
            }
        });
    }

    public event EventHandler? Initialized;
    public bool IsInitialized { get; private set; }

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
            LogManager.Error(LogTag.Account, "account scan failed", ex);
            snapshot = new List<AccountDescriptor>();
        }

        lock (_gate) { _descriptors = snapshot; }

        LogManager.Info(LogTag.Account, $"scanned {snapshot.Count} descriptors from {AccountsFolder}");
    }

    private IEnumerable<AccountDescriptor> ExpandFirmFile(string path)
    {
        var firm = LoadFirm(path);
        if (firm == null) yield break;

        var stem = Path.GetFileName(path)[..^Extension.Length];
        int count = 0;

        foreach (var plan in firm.Plans)
        {
            foreach (var size in plan.Sizes)
            {
                count++;
                yield return new AccountDescriptor(
                    FilePath: path,
                    FileStem: stem,
                    Firm: firm.Firm,
                    Plan: plan.Plan,
                    Size: size.Size,
                    StartingCapital: size.StartingCapital);
            }
        }

        LogManager.Debug(LogTag.Account, $"firm file {stem} → {count} descriptors");
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
            LogManager.Error(LogTag.Account, $"firm load failed: {Path.GetFileName(filePath)}", ex);
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
            LogManager.Error(LogTag.Account,
                $"SetActive failed: could not resolve {descriptor.Firm} {descriptor.Plan} {descriptor.Size}");
            return;
        }

        lock (_gate) { _active = descriptor; }

        _cfg.Settings.ActiveAccountFirmFile = descriptor.FileStem;
        _cfg.Settings.ActiveAccountPlan = descriptor.Plan;
        _cfg.Settings.ActiveAccountSize = descriptor.Size;
        _cfg.SaveConfig();

        LogManager.Info(LogTag.Account,
            $"active: {descriptor.Firm} · {descriptor.Plan} · {descriptor.Size}");
        ActiveChanged?.Invoke(this, descriptor);
    }

    /// <summary>
    /// Resolves the active account into its funded phase. Returns null when
    /// there is no active account, no matching PlanSize, or the PlanSize has
    /// no phases. The funded phase is the LAST entry in Phases by convention
    /// (Phase 1 / Phase 2 / Funded). Never assume phase 0.
    /// </summary>
    public ActiveFundedContext? ResolveActiveFundedContext()
    {
        var active = Active;
        if (active == null) return null;

        var planSize = ResolvePlanSize(active);
        if (planSize == null || planSize.Phases.Count == 0) return null;

        return new ActiveFundedContext(active, planSize, planSize.Phases[planSize.Phases.Count - 1]);
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
            LogManager.Warn(LogTag.Account,
                $"active account '{stem} / {plan} / {size}' referenced in settings but not found on disk");
            return;
        }

        lock (_gate) { _active = match; }
        LogManager.Debug(LogTag.Account, $"restored active from settings: {match.Firm} {match.Plan} {match.Size}");
    }
}