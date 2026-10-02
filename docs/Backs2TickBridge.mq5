//+------------------------------------------------------------------+
//|                                            Backs2TickBridge.mq5  |
//|                    MT5 raw tick data TCP client (EA, not script) |
//|                                                                  |
//|  NOTE: BridgeHost/BridgePort must match AppSettings.Mt5Host and  |
//|  AppSettings.Mt5Port in backs2's config/settings.json. Edit both  |
//|  sides if you change the port.                                    |
//|                                                                  |
//|  v1.16 — resumable COUNT_TICKS.                                    |
//|    COUNT_TICKS accepts an optional "chunk_ms" field. When present,|
//|    the EA splits [from_msc, to_msc) into time chunks, processes   |
//|    one chunk per OnTimer tick, sends unsolicited PROGRESS messages|
//|    between chunks, and emits the final response only after the    |
//|    last chunk. This lets C# observe liveness during long ranges.  |
//|    Absent / zero chunk_ms → monolith mode (v1.13 behaviour).      |
//|                                                                  |
//|    PROGRESS message shape:                                        |
//|      {"id":0,"action":"PROGRESS","request_id":N,                  |
//|       "chunks_done":i,"chunks_total":K,"cumulative_ticks":C}      |
//|                                                                  |
//|    On chunk failure the EA halves chunk_ms and retries the same   |
//|    range (down to MIN_CHUNK_MS). Below that it errors out.        |
//|                                                                  |
//|    A new chunked request pre-empts an in-flight one (latest-wins).|
//+------------------------------------------------------------------+
#property copyright "backs2"
#property version   "1.16"
#property description "Connects to the backs2 C# server and serves raw MqlTick data"
#property strict

// ---- Bootstrap (hardcoded, no inputs) ----
#define BRIDGE_HOST           "127.0.0.1"
#define BRIDGE_PORT           5555
#define BRIDGE_TIMEOUT_MS     5000
#define COPYTICKS_CAP         2000      // hard MQL5 cap per CopyTicks call
#define MAX_REQUEST_TICKS     50000     // EA-side batching ceiling per request
#define RECONNECT_DELAY       3
#define HEARTBEAT_INTERVAL_MS 5000      // idle heartbeat cadence
#define MIN_CHUNK_MS          60000     // 1 minute — floor on adaptive shrink
#define PROGRESS_MIN_GAP_MS   200       // throttle progress messages to ~5 Hz

int    g_socket    = INVALID_HANDLE;
string g_readBuffer = "";
datetime g_lastReconnectAttempt = 0;
bool   g_timerLogged = false;
uint   g_lastHeartbeatTickMs = 0;

// ---- Pending chunked COUNT_TICKS state ----
bool   g_pending_active        = false;
string g_pending_id            = "";
string g_pending_symbol        = "";
long   g_pending_from_msc      = 0;
long   g_pending_to_msc        = 0;
long   g_pending_chunk_ms      = 0;
long   g_pending_current_msc   = 0;
int    g_pending_total_chunks  = 0;
int    g_pending_chunks_done   = 0;
long   g_pending_cumulative    = 0;
long   g_pending_first_msc     = 0;
long   g_pending_last_msc      = 0;
ulong  g_pending_perf_us       = 0;
uint   g_pending_last_progress = 0;

//+------------------------------------------------------------------+
int OnInit()
{
   Print(">>> Backs2TickBridge v1.16: OnInit ENTRY");
   if(!ConnectToServer())
      Print(">>> Backs2TickBridge: initial connection failed, will retry in OnTimer");
   else
      Print(">>> Backs2TickBridge: initial connection OK");
   EventSetMillisecondTimer(10);
   return INIT_SUCCEEDED;
}

//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
   EventKillTimer();
   CloseSocket();
   Print(">>> Backs2TickBridge stopped, reason=", reason);
}

//+------------------------------------------------------------------+
void OnTimer()
{
   if(!g_timerLogged)
   {
      Print(">>> Backs2TickBridge: OnTimer first call, connected=",
            (g_socket != INVALID_HANDLE ? SocketIsConnected(g_socket) : false));
      g_timerLogged = true;
   }

   if(g_socket == INVALID_HANDLE || !SocketIsConnected(g_socket))
   {
      if(TimeCurrent() - g_lastReconnectAttempt >= RECONNECT_DELAY)
      {
         g_lastReconnectAttempt = TimeCurrent();
         ConnectToServer();
      }
      return;
   }

   while(true)
   {
      uint available = SocketIsReadable(g_socket);
      if(available == 0) break;

      uchar buf[];
      int bytesRead = SocketRead(g_socket, buf, available, 100);
      if(bytesRead <= 0) break;

      g_readBuffer += CharArrayToString(buf, 0, bytesRead);
   }

   int newlinePos;
   while((newlinePos = StringFind(g_readBuffer, "\n")) >= 0)
   {
      string line = StringSubstr(g_readBuffer, 0, newlinePos);
      g_readBuffer = StringSubstr(g_readBuffer, newlinePos + 1);
      StringTrimLeft(line);
      StringTrimRight(line);
      if(line != "")
         ProcessRequest(line);
   }

   // Process at most ONE pending chunk per tick. The chunk itself blocks
   // (CopyTicksRange is synchronous), so between chunks we return control
   // to the terminal — the PROGRESS messages emitted here are the only
   // liveness signal during a long running count.
   if(g_pending_active)
      ProcessPendingChunk();

   // Idle heartbeat. Suppressed while a chunked task is active because
   // PROGRESS already provides the liveness signal, and we do not want
   // to interleave two unsolicited message streams on the wire.
   if(!g_pending_active)
   {
      uint nowTick = GetTickCount();
      if(nowTick - g_lastHeartbeatTickMs >= HEARTBEAT_INTERVAL_MS)
      {
         g_lastHeartbeatTickMs = nowTick;
         SendHeartbeat();
      }
   }
}

//+------------------------------------------------------------------+
bool ConnectToServer()
{
   CloseSocket();
   g_socket = SocketCreate();
   if(g_socket == INVALID_HANDLE) return false;

   if(!SocketConnect(g_socket, BRIDGE_HOST, BRIDGE_PORT, BRIDGE_TIMEOUT_MS))
   {
      SocketClose(g_socket);
      g_socket = INVALID_HANDLE;
      return false;
   }

   g_readBuffer = "";
   g_lastHeartbeatTickMs = GetTickCount();
   Print(">>> Backs2TickBridge: CONNECTED to ", BRIDGE_HOST, ":", BRIDGE_PORT);
   return true;
}

//+------------------------------------------------------------------+
void CloseSocket()
{
   if(g_socket != INVALID_HANDLE)
   {
      SocketClose(g_socket);
      g_socket = INVALID_HANDLE;
   }
   g_readBuffer = "";
   // A dropped connection invalidates any in-flight chunked work — the
   // C# side will treat the missing final response as a timeout and its
   // own request waiter is gone anyway.
   if(g_pending_active)
   {
      Print(">>> Backs2TickBridge: aborting pending chunked request (id=",
            g_pending_id, ") due to disconnect");
      g_pending_active = false;
   }
}

//+------------------------------------------------------------------+
void SendHeartbeat()
{
   if(g_socket == INVALID_HANDLE || !SocketIsConnected(g_socket)) return;
   string msg = "{\"id\":0,\"action\":\"HEARTBEAT\",\"ts\":"
                + IntegerToString((long)TimeCurrent()) + "}\n";
   uchar buf[];
   int len = StringToCharArray(msg, buf) - 1;
   if(len <= 0) return;
   SocketSend(g_socket, buf, (uint)len);
}

//+------------------------------------------------------------------+
void ProcessRequest(string json)
{
   string action = JsonGetString(json, "action");
   string id     = ExtractId(json);

   if(action == "PING")
      SendResponse(id, "\"status\":\"OK\"");
   else if(action == "GET_TICKS")
      HandleGetTicks(id, json);
   else if(action == "COUNT_TICKS")
      HandleCountTicks(id, json);
   else if(action == "PREWARM")
      HandlePrewarm(id, json);
   else if(action == "GET_TERMINAL_INFO")
      HandleTerminalInfo(id);
   else
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Unknown action: " + action + "\"");
}

//+------------------------------------------------------------------+
// Serialization uses a uchar[] byte buffer (v1.09). MQL5's string
// concat primitives (both `+=` and StringAdd) reallocate on every
// append — measured O(N²): 20k ticks took 40 s. Writing into a
// pre-sized byte buffer and SocketSend'ing it directly is O(N):
// the same 20k ticks serialize in ~14 ms.
//
// v1.14 removes the per-tick key strings. The "ticks" array is now a
// flat sequence of 7 numbers per tick, in fixed order:
//   time_msc, bid, ask, last, volume, volume_real, flags
// which cuts ~7 short AppendStr calls per tick to zero.
//+------------------------------------------------------------------+
void AppendStr(uchar &dest[], int &pos, string s)
{
   int len = StringLen(s);
   if(len <= 0) return;

   if(pos + len + 32 > ArraySize(dest))
      ArrayResize(dest, pos + len + 8192);

   for(int i = 0; i < len; i++)
      dest[pos++] = (uchar)StringGetCharacter(s, i);
}

//+------------------------------------------------------------------+
void HandlePrewarm(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   long   count   = JsonGetLong(json, "count");

   if(symbol == "")
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Missing symbol\"");
      return;
   }

   if(count <= 0)             count = 500;
   if(count > COPYTICKS_CAP) count = COPYTICKS_CAP;

   ulong cursor = (fromMsc > 0)
      ? (ulong)fromMsc
      : (ulong)(TimeCurrent() - 86400) * 1000;

   MqlTick warm[];
   ulong t0 = GetMicrosecondCount();
   int copied = CopyTicks(symbol, warm, COPY_TICKS_ALL, cursor, (uint)count);
   ulong elapsed = GetMicrosecondCount() - t0;
   int err = GetLastError();

   string body = "\"status\":\"OK\",\"warm_us\":";
   body += IntegerToString((long)elapsed);
   body += ",\"warm_count\":";
   body += IntegerToString(copied);
   body += ",\"warm_err\":";
   body += IntegerToString(err);
   SendResponse(id, body);
}

//+------------------------------------------------------------------+
// COUNT_TICKS (v1.16):
//   Two modes, selected by the optional "chunk_ms" field.
//
//   Monolith (chunk_ms absent or 0):
//     One CopyTicksRange call, one response. Blocks OnTimer for the
//     duration of the call. Unchanged from v1.13.
//
//   Chunked (chunk_ms > 0):
//     Splits [from_msc, to_msc) into ceil(span/chunk_ms) pieces. Each
//     OnTimer tick processes exactly one piece. Between pieces the EA
//     emits an unsolicited PROGRESS message. On the final piece the EA
//     emits the same response shape as monolith mode, plus a "chunks"
//     field reporting how many chunks were used.
//
//     On chunk failure the EA halves chunk_ms and retries the same
//     range. Below MIN_CHUNK_MS it gives up and reports the error.
//
//   The EA rejects a chunked request if a chunked request is already
//   in flight — but pre-empts it (latest-wins) to keep the bridge from
//   locking up on a stale range.
//+------------------------------------------------------------------+
void HandleCountTicks(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   long   toMsc   = JsonGetLong(json, "to_msc");
   long   chunkMs = JsonGetLong(json, "chunk_ms");

   if(symbol == "")
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Missing symbol\"");
      return;
   }
   if(toMsc <= fromMsc)
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"to_msc must be > from_msc\"");
      return;
   }

   if(chunkMs <= 0)
   {
      // Monolith mode — original v1.13 behaviour.
      MqlTick ticks[];
      ulong t0 = GetMicrosecondCount();
      int count = CopyTicksRange(symbol, ticks, COPY_TICKS_ALL,
                                 (ulong)fromMsc, (ulong)toMsc);
      ulong perfUs = GetMicrosecondCount() - t0;
      int err = GetLastError();

      if(count <= 0)
      {
         string body = "\"status\":\"ERROR\",\"message\":\"CopyTicksRange returned ";
         body += IntegerToString(count);
         body += ", error ";
         body += IntegerToString(err);
         body += "\"";
         SendResponse(id, body);
         return;
      }

      long firstMsc = (long)ticks[0].time_msc;
      long lastMsc  = (long)ticks[count - 1].time_msc;

      string body = "\"status\":\"OK\",\"count\":";
      body += IntegerToString(count);
      body += ",\"first_msc\":";
      body += IntegerToString(firstMsc);
      body += ",\"last_msc\":";
      body += IntegerToString(lastMsc);
      body += ",\"perf_us\":";
      body += IntegerToString((long)perfUs);
      SendResponse(id, body);
      return;
   }

   // Chunked mode. Latest-wins: pre-empt any in-flight chunked task.
   if(g_pending_active)
   {
      Print(">>> Backs2TickBridge: pre-empting in-flight chunked request id=",
            g_pending_id, " with new id=", id);
      g_pending_active = false;
   }

   g_pending_active        = true;
   g_pending_id            = id;
   g_pending_symbol        = symbol;
   g_pending_from_msc      = fromMsc;
   g_pending_to_msc        = toMsc;
   g_pending_chunk_ms      = chunkMs;
   g_pending_current_msc   = fromMsc;
   g_pending_total_chunks  = (int)((toMsc - fromMsc + chunkMs - 1) / chunkMs);
   g_pending_chunks_done   = 0;
   g_pending_cumulative    = 0;
   g_pending_first_msc     = 0;
   g_pending_last_msc      = 0;
   g_pending_perf_us       = 0;
   g_pending_last_progress = 0;

   Print(">>> Backs2TickBridge: COUNT_TICKS id=", id, " starting chunked mode — ",
         IntegerToString(g_pending_total_chunks), " chunks × ",
         IntegerToString(chunkMs), "ms");
   // No immediate response — the first PROGRESS message is the ack.
   // The final OK/ERROR response carries this same id.
}

//+------------------------------------------------------------------+
// Process exactly one chunk of the pending chunked COUNT_TICKS. Returns
// after a single CopyTicksRange call so OnTimer can service the socket
// and fire PROGRESS messages between chunks.
//+------------------------------------------------------------------+
void ProcessPendingChunk()
{
   if(!g_pending_active) return;

   long chunk_from = g_pending_current_msc;
   long chunk_to   = chunk_from + g_pending_chunk_ms;
   if(chunk_to > g_pending_to_msc) chunk_to = g_pending_to_msc;

   MqlTick ticks[];
   ulong t0 = GetMicrosecondCount();
   int count = CopyTicksRange(g_pending_symbol, ticks, COPY_TICKS_ALL,
                              (ulong)chunk_from, (ulong)chunk_to);
   ulong elapsed = GetMicrosecondCount() - t0;
   int err = GetLastError();
   g_pending_perf_us += elapsed;

   if(count < 0)
   {
      // Chunk failed. Try a smaller chunk before giving up. This handles
      // the "broker range too dense to allocate in one MqlTick[]" case
      // as well as transient terminal errors.
      if(g_pending_chunk_ms > MIN_CHUNK_MS)
      {
         long newChunk = g_pending_chunk_ms / 2;
         if(newChunk < MIN_CHUNK_MS) newChunk = MIN_CHUNK_MS;
         Print(">>> Backs2TickBridge: chunk failed (count=", count,
               " err=", err, ") — shrinking chunk_ms ",
               IntegerToString(g_pending_chunk_ms), " → ",
               IntegerToString(newChunk));
         g_pending_chunk_ms = newChunk;

         // Recompute remaining chunks with the new size.
         long remainingMs = g_pending_to_msc - g_pending_current_msc;
         int remainingChunks = (int)((remainingMs + g_pending_chunk_ms - 1) / g_pending_chunk_ms);
         g_pending_total_chunks = g_pending_chunks_done + remainingChunks;
         return; // retry on next OnTimer tick
      }

      string body = "\"status\":\"ERROR\",\"message\":\"CopyTicksRange returned ";
      body += IntegerToString(count);
      body += ", error ";
      body += IntegerToString(err);
      body += " (chunk ";
      body += IntegerToString(g_pending_chunks_done + 1);
      body += "/";
      body += IntegerToString(g_pending_total_chunks);
      body += ", chunk_ms=";
      body += IntegerToString(g_pending_chunk_ms);
      body += ")\"";
      SendResponse(g_pending_id, body);
      g_pending_active = false;
      return;
   }

   if(count > 0)
   {
      long firstInChunk = (long)ticks[0].time_msc;
      long lastInChunk  = (long)ticks[count - 1].time_msc;
      if(g_pending_first_msc == 0) g_pending_first_msc = firstInChunk;
      g_pending_last_msc = lastInChunk;
      g_pending_cumulative += count;
   }

   g_pending_current_msc  = chunk_to;
   g_pending_chunks_done += 1;

   bool isFinal = (g_pending_chunks_done >= g_pending_total_chunks)
                  || (g_pending_current_msc >= g_pending_to_msc);

   if(isFinal)
   {
      string body = "\"status\":\"OK\",\"count\":";
      body += IntegerToString(g_pending_cumulative);
      body += ",\"first_msc\":";
      body += IntegerToString(g_pending_first_msc);
      body += ",\"last_msc\":";
      body += IntegerToString(g_pending_last_msc);
      body += ",\"perf_us\":";
      body += IntegerToString((long)g_pending_perf_us);
      body += ",\"chunks\":";
      body += IntegerToString(g_pending_total_chunks);
      SendResponse(g_pending_id, body);

      Print(">>> Backs2TickBridge: COUNT_TICKS id=", g_pending_id,
            " done — ", IntegerToString(g_pending_cumulative),
            " ticks across ", IntegerToString(g_pending_total_chunks),
            " chunk(s)");
      g_pending_active = false;
      return;
   }

   // Throttle PROGRESS to ~5 Hz so a fast chunk stream does not flood
   // the C# UI thread.
   uint nowTick = GetTickCount();
   if(nowTick - g_pending_last_progress < PROGRESS_MIN_GAP_MS) return;
   g_pending_last_progress = nowTick;

   string msg = "{\"id\":0,\"action\":\"PROGRESS\",\"request_id\":";
   msg += g_pending_id;
   msg += ",\"chunks_done\":";
   msg += IntegerToString(g_pending_chunks_done);
   msg += ",\"chunks_total\":";
   msg += IntegerToString(g_pending_total_chunks);
   msg += ",\"cumulative_ticks\":";
   msg += IntegerToString(g_pending_cumulative);
   msg += "}\n";

   uchar buf[];
   int len = StringToCharArray(msg, buf) - 1;
   if(len <= 0) return;
   if(g_socket == INVALID_HANDLE || !SocketIsConnected(g_socket)) return;
   SocketSend(g_socket, buf, (uint)len);
}

//+------------------------------------------------------------------+
// v1.12: correct handling of ticks that share a millisecond at slice
// boundaries.
//
// CopyTicks(from_msc, count) is INCLUSIVE: it returns ticks with
// time_msc >= from_msc. After a slice that ends with K ticks at time
// T, the next CopyTicks(from=T) call re-emits ALL ticks at time T.
//
// v1.11 dropped only the FIRST re-emitted tick, leaving K-1 dupes per
// boundary. Measured: +914 extra ticks across 48 boundaries in a 962k
// fetch (~2 ticks per ms at boundary times).
//
// v1.12 tracks the trailing same-ms count on each slice and skips
// exactly that many leading ticks on the next slice.
//
// v1.14: the "ticks" array body is a flat numeric sequence, 7 numbers
// per tick, in order:
//   time_msc, bid, ask, last, volume, volume_real, flags
// The envelope fields (id, status, next_from_msc, perf_copy_us,
// perf_ser_us) are unchanged.
//+------------------------------------------------------------------+
void HandleGetTicks(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   long   requested = JsonGetLong(json, "count");

   if(symbol == "")
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Missing symbol\"");
      return;
   }

   if(requested <= 0)              requested = 200;
   if(requested > MAX_REQUEST_TICKS) requested = MAX_REQUEST_TICKS;

   ulong cursor = (fromMsc > 0)
      ? (ulong)fromMsc
      : (ulong)(TimeCurrent() - 86400) * 1000;

   int want = (int)requested;

   MqlTick collected[];
   ArrayResize(collected, want);

   int    total           = 0;
   ulong  perfCopyUs      = 0;
   bool   endOfHist       = false;
   long   lastTimeMsc     = -1;
   int    lastTimeTrailing = 0;

   while(total < want && !endOfHist)
   {
      int remaining = want - total;
      int thisBatch = (remaining < COPYTICKS_CAP) ? remaining : COPYTICKS_CAP;

      MqlTick temp[];
      ulong t0 = GetMicrosecondCount();
      int copied = CopyTicks(symbol, temp, COPY_TICKS_ALL, cursor, (uint)thisBatch);
      perfCopyUs += GetMicrosecondCount() - t0;
      int copyErr = GetLastError();

      if(copied <= 0)
      {
         if(total == 0)
         {
            SendResponse(id, "\"status\":\"ERROR\",\"message\":\"CopyTicks returned " +
                         IntegerToString(copied) + ", error " + IntegerToString(copyErr) + "\"");
            return;
         }
         break;
      }

      int startIdx = 0;
      if(lastTimeMsc >= 0 && lastTimeTrailing > 0)
      {
         int skipped = 0;
         while(startIdx < copied
               && (long)temp[startIdx].time_msc == lastTimeMsc
               && skipped < lastTimeTrailing)
         {
            startIdx++;
            skipped++;
         }
      }

      int newCount = copied - startIdx;
      if(newCount <= 0)
      {
         cursor = (ulong)temp[copied - 1].time_msc + 1;
         if(copied < thisBatch) { endOfHist = true; break; }
         continue;
      }

      int take = newCount;
      if(total + take > want) take = want - total;

      ArrayCopy(collected, temp, total, startIdx, take);
      total += take;

      long sliceLastTime = (long)temp[copied - 1].time_msc;
      int sliceTrailing = 0;
      for(int i = copied - 1; i >= startIdx; i--)
      {
         if((long)temp[i].time_msc == sliceLastTime) sliceTrailing++;
         else break;
      }
      lastTimeMsc      = sliceLastTime;
      lastTimeTrailing = sliceTrailing;

      if(copied < thisBatch) { endOfHist = true; break; }

      cursor = (ulong)temp[copied - 1].time_msc;
   }

   long lastEmittedTimeMsc = (total > 0) ? (long)collected[total - 1].time_msc : 0;

   // Compact form: ~90 bytes per tick worst case (13-digit time_msc,
   // 6 numbers, 8-decimal doubles). AppendStr grows on demand; the
   // estimate just avoids early resizes.
   int cap = total * 128 + 512;
   uchar buf[];
   ArrayResize(buf, cap);
   int pos = 0;

   AppendStr(buf, pos, "{\"id\":");
   AppendStr(buf, pos, id);
   AppendStr(buf, pos, ",\"status\":\"OK\",\"next_from_msc\":");
   AppendStr(buf, pos, IntegerToString(lastEmittedTimeMsc));
   AppendStr(buf, pos, ",\"perf_copy_us\":");
   AppendStr(buf, pos, IntegerToString((long)perfCopyUs));
   AppendStr(buf, pos, ",\"ticks\":[");

   ulong serStart = GetMicrosecondCount();

   for(int i = 0; i < total; i++)
   {
      if(i > 0) AppendStr(buf, pos, ",");
      AppendStr(buf, pos, IntegerToString((long)collected[i].time_msc));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, DoubleToString(collected[i].bid, 8));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, DoubleToString(collected[i].ask, 8));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, DoubleToString(collected[i].last, 8));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, IntegerToString((long)collected[i].volume));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, DoubleToString(collected[i].volume_real, 8));
      AppendStr(buf, pos, ",");
      AppendStr(buf, pos, IntegerToString((int)collected[i].flags));
   }

   ulong serUs = GetMicrosecondCount() - serStart;

   AppendStr(buf, pos, "],\"perf_ser_us\":");
   AppendStr(buf, pos, IntegerToString((long)serUs));
   AppendStr(buf, pos, "}\n");

   if(g_socket != INVALID_HANDLE && SocketIsConnected(g_socket))
      SocketSend(g_socket, buf, (uint)pos);
}

//+------------------------------------------------------------------+
void HandleTerminalInfo(string id)
{
   string body = "\"status\":\"OK\"";
   body += ",\"company\":\""   + JsonEscape(AccountInfoString(ACCOUNT_COMPANY))  + "\"";
   body += ",\"account\":"     + IntegerToString(AccountInfoInteger(ACCOUNT_LOGIN));
   body += ",\"server\":\""    + JsonEscape(AccountInfoString(ACCOUNT_SERVER))   + "\"";
   body += ",\"currency\":\""  + JsonEscape(AccountInfoString(ACCOUNT_CURRENCY)) + "\"";
   body += ",\"leverage\":"    + IntegerToString(AccountInfoInteger(ACCOUNT_LEVERAGE));
   body += ",\"server_time\":" + IntegerToString((long)TimeCurrent());
   SendResponse(id, body);
}

//+------------------------------------------------------------------+
void SendResponse(string id, string body)
{
   if(g_socket == INVALID_HANDLE || !SocketIsConnected(g_socket)) return;
   string msg = "{\"id\":" + id + "," + body + "}\n";
   uchar buf[];
   int len = StringToCharArray(msg, buf) - 1;
   if(len <= 0) return;
   SocketSend(g_socket, buf, (uint)len);
}

//+------------------------------------------------------------------+
string ExtractId(string json)
{
   int pos = StringFind(json, "\"id\":");
   if(pos < 0) return "0";
   pos += 5;
   while(pos < StringLen(json) && StringGetCharacter(json, pos) == ' ') pos++;
   int end = pos;
   while(end < StringLen(json))
   {
      ushort c = StringGetCharacter(json, end);
      if((c >= '0' && c <= '9') || c == '-') end++;
      else break;
   }
   if(end <= pos) return "0";
   return StringSubstr(json, pos, end - pos);
}

//+------------------------------------------------------------------+
string JsonGetString(string json, string key)
{
   string pattern = "\"" + key + "\"";
   int pos = StringFind(json, pattern);
   if(pos < 0) return "";
   pos = StringFind(json, ":", pos + StringLen(pattern));
   if(pos < 0) return "";
   pos++;
   while(pos < StringLen(json) && StringGetCharacter(json, pos) == ' ') pos++;
   if(pos >= StringLen(json) || StringGetCharacter(json, pos) != '"') return "";
   pos++;
   int end = StringFind(json, "\"", pos);
   if(end < 0) return "";
   return StringSubstr(json, pos, end - pos);
}

//+------------------------------------------------------------------+
long JsonGetLong(string json, string key)
{
   string pattern = "\"" + key + "\"";
   int pos = StringFind(json, pattern);
   if(pos < 0) return 0;
   pos = StringFind(json, ":", pos + StringLen(pattern));
   if(pos < 0) return 0;
   pos++;
   while(pos < StringLen(json) && StringGetCharacter(json, pos) == ' ') pos++;
   int end = pos;
   while(end < StringLen(json))
   {
      ushort c = StringGetCharacter(json, end);
      if((c >= '0' && c <= '9') || c == '-') end++;
      else break;
   }
   if(end <= pos) return 0;
   return StringToInteger(StringSubstr(json, pos, end - pos));
}

//+------------------------------------------------------------------+
string JsonEscape(string s)
{
   StringReplace(s, "\\", "\\\\");
   StringReplace(s, "\"", "\\\"");
   StringReplace(s, "\n", "\\n");
   StringReplace(s, "\r", "\\r");
   return s;
}
//+------------------------------------------------------------------+