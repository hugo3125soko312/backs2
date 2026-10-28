using System;
using System.Text.Json.Serialization;
using backs2.Core.Data;

namespace backs2.Core.Models;

// Disk format: envelope around KlineCompact[]. The envelope carries provenance
// (which provider produced the data) and keeps the bars themselves in the compact
// flat form. Legacy files that are a bare KlineCompact[] are rejected by
// CacheManager.LoadCacheAsync — data refetches on next call.
public sealed record CacheEnvelope(
    string Source,
    DateTime FetchedUtc,
    string Symbol,
    string Timeframe,
    KlineCompact[] Bars,
    DataOrigin Origin = DataOrigin.Legacy);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(CacheEnvelope))]
[JsonSerializable(typeof(DataOrigin))]
[JsonSerializable(typeof(KlineCompact[]))]
internal partial class KlineJsonContext : JsonSerializerContext
{
}