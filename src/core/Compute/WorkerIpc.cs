using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.Compute;

public static class WorkerIpc
{
    // Hard ceiling on a single frame. Guards against a corrupted length
    // prefix causing a multi-gigabyte allocation attempt.
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(Stream stream, WorkerEnvelope message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidOperationException($"Frame too large: {payload.Length} bytes");

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, payload.Length);

        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<WorkerEnvelope?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidOperationException($"Invalid frame length: {length}");

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream closed mid-frame");

        return JsonSerializer.Deserialize<WorkerEnvelope>(payload, JsonOptions);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException("Unexpected end of stream");
            }
            total += read;
        }
        return true;
    }
}