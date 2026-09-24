using System.Text.Json.Serialization;

namespace backs2.Core.Accounts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AccountConfig))]
internal partial class AccountJsonContext : JsonSerializerContext
{
}