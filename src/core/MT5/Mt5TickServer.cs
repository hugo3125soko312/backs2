using System;
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

    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
    private int _nextRequestId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool IsClientConnected => _client?.Connected == true;

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

        var buffer = new byte[65536];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                {
                    LogManager.Debug(LogTag.Provider, "MT5: EA closed the socket (0 bytes)");
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                int newlineIdx;
                while ((newlineIdx = sb.ToString().IndexOf('\n')) >= 0)
                {
                    var line = sb.ToString(0, newlineIdx).Trim();
                    sb.Remove(0, newlineIdx + 1);
                    if (line.Length > 0)
                    {
                        LogManager.Debug(LogTag.Provider,
                            $"MT5 ← raw ({line.Length} chars): {Truncate(line, 300)}");
                        DispatchResponse(line);
                    }
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

    private void DispatchResponse(string json)
    {
        int idStart = json.IndexOf("\"id\":");
        if (idStart < 0)
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: response with no id field, dropping: {Truncate(json, 200)}");
            return;
        }

        int numStart = idStart + 5;
        int numEnd = numStart;
        while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-'))
            numEnd++;

        if (!int.TryParse(json.Substring(numStart, numEnd - numStart), out int id))
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: response id unparseable, dropping: {Truncate(json, 200)}");
            return;
        }

        if (_pending.TryRemove(id, out var tcs))
        {
            LogManager.Debug(LogTag.Provider, $"MT5: routing response id={id} to waiter");
            tcs.TrySetResult(json);
        }
        else
        {
            LogManager.Warn(LogTag.Provider,
                $"MT5: no waiter for id={id}, orphan response dropped");
        }
    }

    public async Task<string?> SendRequestAsync(string action, string extraJson, int timeoutMs, CancellationToken ct)
    {
        if (_stream == null)
        {
            LogManager.Warn(LogTag.Provider, $"MT5: SendRequestAsync called but stream is null");
            return null;
        }

        int id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            LogManager.Warn(LogTag.Provider,
                $"MT5: request id={id} action={action} timed out after {timeoutMs}ms");
            return null;
        }

        return await tcs.Task;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => Stop();
}