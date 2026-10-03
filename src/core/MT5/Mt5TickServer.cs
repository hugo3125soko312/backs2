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

    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pending = new();
    private int _nextRequestId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    public bool IsClientConnected => _client?.Connected == true;
    public DateTime LastHeartbeatUtc => _lastHeartbeatUtc;
    public bool HasHeartbeat => _lastHeartbeatUtc != DateTime.MinValue;
    public TimeSpan LastHeartbeatAge =>
        HasHeartbeat ? DateTime.UtcNow - _lastHeartbeatUtc : TimeSpan.MaxValue;

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
        if (jsonBytes.AsSpan().IndexOf("\"action\":\"HEARTBEAT\""u8) >= 0)
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
            LogManager.Debug(LogTag.Provider, "MT5: heartbeat from EA");
            return;
        }

        if (jsonBytes.AsSpan().IndexOf("\"action\":\"PROGRESS\""u8) >= 0)
        {
            if (TryParseProgress(jsonBytes, out var progress))
            {
                LogManager.Debug(LogTag.Provider,
                    $"MT5: progress chunk {progress.ChunksDone}/{progress.ChunksTotal} " +
                    $"(cumulative {progress.CumulativeTicks:N0}, synced {progress.SyncedDays}, " +
                    $"skipped {progress.SkippedDays})");
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

        if (!TryReadInt(jsonBytes, "request_id"u8, out int requestId)) requestId = 0;
        if (!TryReadInt(jsonBytes, "chunks_done"u8, out int done)) return false;
        if (!TryReadInt(jsonBytes, "chunks_total"u8, out int total)) return false;

        // cumulative_ticks ships with COUNT_TICKS progress; synced_days and
        // skipped_days ship with SYNC_TICKS_RANGE progress. Neither is
        // mandatory — a missing field means "not applicable for this
        // action", not "malformed message". Defaulting to 0 keeps both
        // shapes flowing through the same event.
        if (!TryReadLong(jsonBytes, "cumulative_ticks"u8, out long cumulative)) cumulative = 0;
        if (!TryReadInt(jsonBytes, "synced_days"u8, out int synced)) synced = 0;
        if (!TryReadInt(jsonBytes, "skipped_days"u8, out int skipped)) skipped = 0;

        progress = new Mt5CountProgress(requestId, done, total, cumulative, synced, skipped);
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