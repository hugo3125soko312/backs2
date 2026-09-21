using System.Text.Json.Serialization;

namespace backs2.Core.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(KlineCompact[]))]
internal partial class KlineJsonContext : JsonSerializerContext
{
}