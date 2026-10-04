namespace backs2.Core.Compute;

using System.Collections.Generic;

public sealed record JobConfig(
    string ScriptPath,
    string CachePath,
    IReadOnlyDictionary<string, string> Params,
    int MemoryLimitMb);