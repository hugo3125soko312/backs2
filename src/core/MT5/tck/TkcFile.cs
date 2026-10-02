// src/core/MT5/Tkc/TkcFile.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace backs2.Core.MT5.Tkc;

/// <summary>
/// Opened .tkc file. Decodes lazily on first access and caches the result.
/// NOT thread-safe — construct one per caller, or wrap in a lock.
/// </summary>
public sealed class TkcFile
{
    private readonly string _path;
    private TkcInfo? _info;
    private IReadOnlyList<TkcTick>? _ticks;

    public TkcFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException($"TKC file not found: {path}", path);

        _path = path;
    }

    public string Path => _path;

    public TkcInfo Info => EnsureDecoded();
    public int Count => Ticks.Count;
    public string Symbol => Info.Symbol;

    public IReadOnlyList<TkcTick> Ticks
    {
        get
        {
            EnsureDecoded();
            return _ticks!;
        }
    }

    private TkcInfo EnsureDecoded()
    {
        if (_info != null) return _info;

        var (info, ticks) = TkcDecoder.Decode(_path);
        _info = info;
        _ticks = ticks;
        return info;
    }
}

/// <summary>
/// Enumerates the .tkc files for a symbol inside a MetaTrader terminal's
/// Bases directory. Layout matches MT5:
///   {terminalRoot}/bases/{broker}/ticks/{symbol}/YYYYMM.tkc
/// </summary>
public static class TkcRepository
{
    /// <summary>
    /// Lists all .tkc files for a symbol under a given terminal data root,
    /// sorted by filename (which sorts chronologically for YYYYMM.tkc).
    /// </summary>
    public static IReadOnlyList<string> ListSymbolFiles(
        string terminalDataRoot, string broker, string symbol)
    {
        string dir = System.IO.Path.Combine(
            terminalDataRoot, "bases", broker, "ticks", symbol);

        if (!Directory.Exists(dir)) return Array.Empty<string>();

        var files = Directory.GetFiles(dir, "*.tkc");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }
}