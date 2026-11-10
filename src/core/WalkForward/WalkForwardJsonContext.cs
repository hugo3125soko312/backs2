using System.Text.Json.Serialization;

namespace backs2.Core.WalkForward;

/// <summary>
/// Source-generated JSON context for the walk-forward batch archive.
///
/// <para>Differs from <c>OptimizationJsonContext</c> in one crucial way:
/// <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals"/>
/// is on. WF stores NaN/Infinity whenever a metric is undefined (zero
/// trades, empty MC pass), and without the flag the round-trip would throw.</para>
///
/// <para>The existing optimization context is deliberately NOT changed:
/// touching it would put the optimizer archive at risk for no reason.</para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    WriteIndented = false)]
[JsonSerializable(typeof(WalkForwardBatch))]
internal partial class WalkForwardJsonContext : JsonSerializerContext
{
}