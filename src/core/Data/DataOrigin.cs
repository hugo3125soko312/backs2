using System.Text.Json.Serialization;

namespace backs2.Core.Data;

[JsonConverter(typeof(JsonStringEnumConverter<DataOrigin>))]
public enum DataOrigin
{
    Legacy = 0,
    RawTick = 1,
    TimeframeFetch = 2,
}