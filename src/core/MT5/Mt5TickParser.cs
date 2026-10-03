using System;
using System.Collections.Generic;
using System.Text.Json;

namespace backs2.Core.MT5;

public static class Mt5TickParser
{
    public static bool TryParseTicks(
        ReadOnlySpan<byte> json,
        int expectedCount,
        out List<Mt5Tick> ticks,
        out long perfCopyUs,
        out long perfSerUs,
        out long nextFromMsc,
        out string? error)
    {
        ticks = new List<Mt5Tick>(expectedCount);
        perfCopyUs = 0;
        perfSerUs = 0;
        nextFromMsc = 0;
        error = null;

        string? status = null;
        string? message = null;

        var reader = new Utf8JsonReader(json);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            error = "expected JSON object at top level";
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                error = $"unexpected token {reader.TokenType}";
                return false;
            }

            if (reader.ValueTextEquals("status"u8))
            {
                reader.Read();
                status = reader.GetString();
            }
            else if (reader.ValueTextEquals("message"u8))
            {
                reader.Read();
                message = reader.GetString();
            }
            else if (reader.ValueTextEquals("perf_copy_us"u8))
            {
                reader.Read();
                perfCopyUs = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("perf_ser_us"u8))
            {
                reader.Read();
                perfSerUs = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("next_from_msc"u8))
            {
                reader.Read();
                nextFromMsc = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("ticks"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    error = "expected array for 'ticks' property";
                    return false;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    long timeMsc = reader.GetInt64();
                    
                    reader.Read();
                    double bid = reader.GetDouble();
                    
                    reader.Read();
                    double ask = reader.GetDouble();
                    
                    reader.Read();
                    double last = reader.GetDouble();
                    
                    reader.Read();
                    ulong volume = (ulong)reader.GetInt64();
                    
                    reader.Read();
                    double volumeReal = reader.GetDouble();
                    
                    reader.Read();
                    uint flags = (uint)reader.GetInt32();

                    ticks.Add(new Mt5Tick(timeMsc, bid, ask, last, volume, volumeReal, flags));
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (status != "OK")
        {
            error = message ?? status ?? "unknown status error";
            return false;
        }

        return true;
    }
}