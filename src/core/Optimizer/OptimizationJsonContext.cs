using System.Text.Json.Serialization;

namespace backs2.Core.Optimizer;

// Source-generated JSON context for the optimization batch archive.
// Mirrors the KlineJsonContext / AccountJsonContext pattern: no reflection
// on the read path, camel-case keys, nulls omitted (so an unrun MC pass
// does not emit a null Mc block).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OptimizationBatch))]
internal partial class OptimizationJsonContext : JsonSerializerContext
{
}