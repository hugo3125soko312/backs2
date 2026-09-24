using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace backs2.Core.Accounts;

public sealed record AccountDescriptor(
    string FilePath,
    string FileStem
);

public sealed class AccountService
{
    private const string AccountsFolder = "data/accounts";
    private const string Extension = ".account.json";

    private readonly ConfigManager _cfg;
    private readonly LogManager _log;
    private readonly object _gate = new();

    private List<AccountDescriptor> _descriptors = new();
    private AccountConfig? _active;

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

    public AccountConfig? Active
    {
        get { lock (_gate) { return _active; } }
    }

    public event EventHandler<AccountConfig?>? ActiveChanged;

    public void Refresh()
    {
        List<AccountDescriptor> snapshot;
        try
        {
            snapshot = Directory
                .EnumerateFiles(AccountsFolder, "*" + Extension, SearchOption.TopDirectoryOnly)
                .Select(path => new AccountDescriptor(
                    FilePath: path,
                    FileStem: Path.GetFileName(path)[..^Extension.Length]))
                .OrderBy(d => d.FileStem, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            snapshot = new List<AccountDescriptor>();
        }

        lock (_gate) { _descriptors = snapshot; }
    }

    public AccountConfig? Load(AccountDescriptor descriptor)
    {
        if (descriptor == null) return null;

        try
        {
            if (!File.Exists(descriptor.FilePath)) return null;
            var json = File.ReadAllText(descriptor.FilePath);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize(json, AccountJsonContext.Default.AccountConfig);
        }
        catch (Exception ex)
        {
            _log.Error($"Account load failed: {descriptor.FileStem} - {ex.Message}");
            return null;
        }
    }

    public AccountConfig? LoadByStem(string fileStem)
    {
        if (string.IsNullOrWhiteSpace(fileStem)) return null;

        AccountDescriptor? match;
        lock (_gate)
        {
            match = _descriptors.FirstOrDefault(d =>
                string.Equals(d.FileStem, fileStem, StringComparison.OrdinalIgnoreCase));
        }

        return match == null ? null : Load(match);
    }

    public void SetActive(AccountDescriptor descriptor)
    {
        var loaded = Load(descriptor);
        if (loaded == null)
        {
            _log.Error($"SetActive failed: could not load {descriptor.FileStem}");
            return;
        }

        SetActive(loaded, descriptor.FileStem);
    }

    public void SetActive(AccountConfig account, string fileStem)
    {
        lock (_gate) { _active = account; }

        _cfg.Settings.ActiveAccountName = fileStem;
        _cfg.SaveConfig();

        _log.Info($"Account active: {account.Name}");
        ActiveChanged?.Invoke(this, account);
    }

    private void TryLoadActiveFromSettings()
    {
        var stem = _cfg.Settings.ActiveAccountName;
        if (string.IsNullOrWhiteSpace(stem)) return;

        var loaded = LoadByStem(stem);
        if (loaded == null)
        {
            _log.Info($"Active account '{stem}' referenced in settings but not found on disk.");
            return;
        }

        lock (_gate) { _active = loaded; }
    }
}