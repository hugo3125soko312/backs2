using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.MT5;

public class Mt5TickServer : IDisposable
{
    private readonly ConfigManager _config;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _readLoop;

    // v1.14: responses are handed to waiters as raw UTF-8 bytes, not strings.
    // The service parses them with Utf8JsonReader on the hot path — no
    // intermediate string, no JsonDocument, no per-field substring.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pending = new();
    private int _nextRequestId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // v1.15: EA heartbeat. Updated from the read-loop thread; read from the
    // UI thread on demand. DateTime is 64-bit and torn reads are harmless
    // here — worst case the UI shows a slightly stale age for one frame.
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    public bool IsClientConnected => _client?.Connected == true;
    public DateTime LastHeartbeatUtc => _lastHeartbeatUtc;
    public bool HasHeartbeat => _lastHeartbeatUtc != DateTime.MinValue;
    public TimeSpan LastHeartbeatAge =>
        HasHeartbeat ? DateTime.UtcNow - _lastHeartbeatUtc : TimeSpan.MaxValue;

    /// <summary>
    /// v1.16: fires on every unsolicited PROGRESS message from the EA during
    /// a chunked COUNT_TICKS. Payload carries cumulative tick count and
    /// chunk index. Subscribers filter by their own request if needed —
    /// currently the EA serializes chunked requests, so any listener can
    /// assume the progress belongs to its in-flight request.
    /// </summary>
    public event Action<Mt5CountProgress>? CountProgress;

    public Mt5TickServer(ConfigManager config)
    {
        _config = config;
    }

    public void Start()
    {
        if (_listener != null) return;

        int port = _config.Settings.Mt5Port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        LogManager.Info(LogTag.Provider, $"MT5: server listening on 127.0.0.1:{port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _client?.Close(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _client = client;
                _stream = client.GetStream();
                LogManager.Info(LogTag.Provider, "MT5: EA connected");
                _readLoop = Task.Run(() => ReadLoopAsync(ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Provider, "MT5: accept loop error", ex);
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream == null) return;

        // Byte-oriented accumulator. 64 KB read buffer, doubling accumulator.
        // One message per read is the normal case; large GET_TICKS responses
        // (200 KB – 3 MB) span several reads and are stitched here.
        var readBuf = new byte[65536];
        var accBuf  = new byte[65536];
        int accCount = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(readBuf, ct);
                if (bytesRead == 0)
                {
                    LogManager.Debug(LogTag.Provider, "MT5: EA closed the socket (0 bytes)");
                    break;
                }

                if (accCount + bytesRead > accBuf.Length)
                {
                    int newSize = Math.Max(accBuf.Length * 2, accCount + bytesRead);
                    var newBuf = new byte[newSize];
                    Buffer.BlockCopy(accBuf, 0, newBuf, 0, accCount);
                    accBuf = newBuf;
                }
                Buffer.BlockCopy(readBuf, 0, accBuf, accCount, bytesRead);
                accCount += bytesRead;

                int scanStart = 0;
                for (int i = 0; i < accCount; i++)
                {
                    if (accBuf[i] != (byte)'\n') continue;

                    int len = i - scanStart;
                    if (len > 0)
                    {
                        var line = new byte[len];
                        Buffer.BlockCopy(accBuf, scanStart, line, 0, len);
                        DispatchResponseBytes(line);
                    }
                    scanStart = i + 1;
                }

                if (scanStart > 0)
                {
                    int remain = accCount - scanStart;
                    if (remain > 0)
                        Buffer.BlockCopy(accBuf, scanStart, accBuf, 0, remain);
                    accCount = remain;
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.Provider, $"MT5: read loop ended: {ex.Message}");
        }
        finally
        {
            LogManager.Warn(LogTag.Provider, "MT5: EA disconnected");
            _client = null;
            _stream = null;
        }
    }

    private void DispatchResponseBytes(byte[] jsonBytes)
    {
        // v1.15: heartbeat. Match on the literal action value.
        if (jsonBytes.AsSpan().IndexOf("\"action\":\"HEARTBEAT\""u8) >= 0)
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
            LogManager.Debug(LogTag.Provider, "MT5: heartbeat from EA");
            return;
        }

        // v1.16: PROGRESS from a chunked COUNT_TICKS. Parse the four
        // numeric fields and fire the event. No id routing — this is an
        // unsolicited push, id is always 0.
        if (jsonBytes.AsSpan().IndexOf("\"action\":\"PROGRESS\""u8) >= 0)
        {
            if (TryParseProgress(jsonBytes, out var progress))
            {
                LogManager.Debug(LogTag.Provider,
                    $"MT5: progress chunk {progress.ChunksDone}/{progress.ChunksTotal} " +
                    $"({progress.CumulativeTicks:N0} ticks)");
                CountProgress?.Invoke(progress);
            }
            else
            {
                LogManager.Warn(LogTag.Provider,
                    $"MT5: PROGRESS parse failed: {Preview(jsonBytes, 200)}");
            }
            return;
        }

        int idStart = jsonBytes.AsSpan().IndexOf("\"id\":"u8);
        if (idStart < 0)
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: response with no id field, dropping ({jsonBytes.Length} bytes): {Preview(jsonBytes, 200)}");
            return;
        }

        int numStart = idStart + 5;
        int numEnd = numStart;
        while (numEnd < jsonBytes.Length &&
               (jsonBytes[numEnd] == (byte)'-' ||
                (jsonBytes[numEnd] >= (byte)'0' && jsonBytes[numEnd] <= (byte)'9')))
            numEnd++;

        if (numEnd == numStart ||
            !Utf8Parser.TryParse(jsonBytes.AsSpan(numStart, numEnd - numStart), out int id, out _))
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: response id unparseable, dropping: {Preview(jsonBytes, 200)}");
            return;
        }

        if (_pending.TryRemove(id, out var tcs))
        {
            LogManager.Debug(LogTag.Provider,
                $"MT5: routing response id={id} to waiter ({jsonBytes.Length} bytes)");
            tcs.TrySetResult(jsonBytes);
        }
        else
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: no waiter for id={id}, orphan response dropped");
        }
    }

    private static bool TryParseProgress(byte[] jsonBytes, out Mt5CountProgress progress)
    {
        progress = default!;

        // Scan for the three integer fields. The wire format is fixed and
        // short (~120 bytes), so a targeted Utf8Parser walk is faster and
        // allocation-free compared to a full Utf8JsonReader.
        if (!TryReadInt(jsonBytes, "chunks_done"u8, out int done)) return false;
        if (!TryReadInt(jsonBytes, "chunks_total"u8, out int total)) return false;
        if (!TryReadLong(jsonBytes, "cumulative_ticks"u8, out long cumulative)) return false;

        progress = new Mt5CountProgress(done, total, cumulative);
        return true;
    }

    private static bool TryReadInt(byte[] json, ReadOnlySpan<byte> key, out int value)
    {
        value = 0;
        int keyIdx = json.AsSpan().IndexOf(key);
        if (keyIdx < 0) return false;

        int colon = keyIdx + key.Length;
        while (colon < json.Length && json[colon] != (byte)':') colon++;
        if (colon >= json.Length) return false;
        colon++;

        while (colon < json.Length && json[colon] == (byte)' ') colon++;
        int end = colon;
        while (end < json.Length &&
               (json[end] == (byte)'-' || (json[end] >= (byte)'0' && json[end] <= (byte)'9')))
            end++;

        return Utf8Parser.TryParse(json.AsSpan(colon, end - colon), out value, out _);
    }

    private static bool TryReadLong(byte[] json, ReadOnlySpan<byte> key, out long value)
    {
        value = 0;
        int keyIdx = json.AsSpan().IndexOf(key);
        if (keyIdx < 0) return false;

        int colon = keyIdx + key.Length;
        while (colon < json.Length && json[colon] != (byte)':') colon++;
        if (colon >= json.Length) return false;
        colon++;

        while (colon < json.Length && json[colon] == (byte)' ') colon++;
        int end = colon;
        while (end < json.Length &&
               (json[end] == (byte)'-' || (json[end] >= (byte)'0' && json[end] <= (byte)'9')))
            end++;

        return Utf8Parser.TryParse(json.AsSpan(colon, end - colon), out value, out _);
    }

    public async Task<byte[]?> SendRequestAsync(string action, string extraJson, int timeoutMs, CancellationToken ct)
    {
        if (_stream == null)
        {
            LogManager.Warn(LogTag.Provider, "MT5: SendRequestAsync called but stream is null");
            return null;
        }

        int id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        string payload = extraJson.Length == 0
            ? $"{{\"id\":{id},\"action\":\"{action}\"}}\n"
            : $"{{\"id\":{id},\"action\":\"{action}\",{extraJson}}}\n";

        LogManager.Debug(LogTag.Provider,
            $"MT5 → raw ({payload.Length} chars, id={id}): {payload.TrimEnd()}");

        await _writeLock.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            string hb = HasHeartbeat
                ? $"{LastHeartbeatAge.TotalSeconds:F1}s ago"
                : "never";
            LogManager.Warn(LogTag.Provider,
                $"MT5: request id={id} action={action} timed out after {timeoutMs}ms (last heartbeat: {hb})");
            return null;
        }

        return await tcs.Task;
    }

    private static string Preview(byte[] bytes, int max)
    {
        int len = Math.Min(max, bytes.Length);
        var s = Encoding.UTF8.GetString(bytes, 0, len);
        return bytes.Length <= max ? s : s + "…";
    }

    public void Dispose() => Stop();
}