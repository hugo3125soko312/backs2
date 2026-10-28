using System;

namespace backs2.Core.Data;

public interface IDataOriginResolver
{
    DataOrigin FromSource(string? source);
    DataOrigin Resolve(string? source, DataOrigin stored);
}

public sealed class DataOriginResolver : IDataOriginResolver
{
    public const string Mt5ParsedSource = "MT5 Parsed";

    public DataOrigin FromSource(string? source) => FromSourceStatic(source);

    public DataOrigin Resolve(string? source, DataOrigin stored)
        => stored != DataOrigin.Legacy ? stored : FromSourceStatic(source);

    public static DataOrigin FromSourceStatic(string? source)
        => string.Equals(source, Mt5ParsedSource, StringComparison.OrdinalIgnoreCase)
            ? DataOrigin.RawTick
            : DataOrigin.TimeframeFetch;
}