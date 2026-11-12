using System.IO;
using System.Text.Json;
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
///
/// <para>Schema Versioning:
/// - 1.0: Initial version
/// - 1.1: Added ConsistencyScore field
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    WriteIndented = false)]
[JsonSerializable(typeof(WalkForwardBatch))]
[JsonSerializable(typeof(WalkForwardWindowResult))]
internal partial class WalkForwardJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Current schema version. Loaders should reject files with older versions.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
    
    /// <summary>
    /// Minimum supported schema version for backward compatibility.
    /// </summary>
    public const int MinSupportedSchemaVersion = 1;
    
    /// <summary>
    /// Deserialize with version validation.
    /// </summary>
    public static WalkForwardBatch? LoadWithVersionValidation(string json)
    {
        var options = new JsonSerializerOptions
        {
            SourceGenerationContext = new WalkForwardJsonContext(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }),
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        
        var batch = JsonSerializer.Deserialize<WalkForwardBatch>(json, options);
        
        if (batch?.SchemaVersion < MinSupportedSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported schema version: {batch?.SchemaVersion}. " +
                $"Minimum supported: {MinSupportedSchemaVersion}");
        
        return batch;
    }
}
