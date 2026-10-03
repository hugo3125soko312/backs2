//+------------------------------------------------------------------+
//|                                            Backs2TickBridge.mq5  |
//|                    MT5 raw tick data TCP client (EA, not script) |
//|                                                                  |
//|  NOTE: BridgeHost/BridgePort must match AppSettings.Mt5Host and  |
//|  AppSettings.Mt5Port in backs2's config/settings.json. Edit both  |
//|  sides if you change the port.                                    |
//|                                                                  |
//|  v1.18 — debug logging + immediate ack.                           |
//|    HandleSyncTicksRange now emits an initial PROGRESS frame the   |
//|    moment the request is accepted (throttle bypassed), so the C#  |
//|    side has positive confirmation the request landed even while   |
//|    the first CopyTicksRange call is blocking on a broker-side     |
//|    history download. Per-day attempts and every outbound frame    |
//|    are now logged.                                                |
//|                                                                  |
//|  v1.17 — SYNC_TICKS_RANGE.                                        |
//|    New action forces the terminal to download a specific range    |
//|    of tick history from the broker and write it into the local    |
//|    .tkc cache. Iterates day-by-day using CopyTicksRange; on       |
//|    ERR_HISTORY_TIMEOUT (4403) retries the same day up to a        |
//|    per-day timeout. Emits PROGRESS messages between days.         |
//|                                                                  |
//|    Request:                                                       |
//|      {"id":N,"action":"SYNC_TICKS_RANGE",                         |
//|       "symbol":"GOLD.pro","from_msc":X,"to_msc":Y}                |
//|                                                                  |
//|    PROGRESS message shape:                                        |
//|      {"id":0,"action":"PROGRESS","request_id":N,                  |
//|       "chunks_done":D,"chunks_total":T,                           |
//|       "synced_days":S,"skipped_days":K}                           |
//|                                                                  |
//|    Final response:                                                |
//|      {"id":N,"status":"OK","synced_days":S,                       |
//|       "skipped_days":K,"total_days":T}                            |
//|                                                                  |
//|    Aborts if TERMINAL_CONNECTED goes false. A new chunked request |
//|    pre-empts an in-flight one (latest-wins).                      |
//|                                                                  |
//|  v1.16 — resumable COUNT_TICKS.                                    |
//|    COUNT_TICKS accepts an optional "chunk_ms" field. When present,|
//|    the EA splits [from_msc, to_msc) into time chunks, processes   |
//|    one chunk per OnTimer tick, sends unsolicited PROGRESS messages|
//|    between chunks, and emits the final response only after the    |
//|    last chunk.                                                    |
//|                                                                  |
//|    On chunk failure the EA halves chunk_ms and retries the same   |
//|    range (down to MIN_CHUNK_MS). Below that it errors out.        |
//+------------------------------------------------------------------+
#property copyright "backs2"
#property version   "1.18"
#property description "Connects to the backs2 C# server and serves raw MqlTick data"
#property strict

// ---- Bootstrap (hardcoded, no inputs) ----
#define BRIDGE_HOST              "127.0.0.1"
#define BRIDGE_PORT              5555
#define BRIDGE_TIMEOUT_MS        5000
#define COPYTICKS_CAP            2000      // hard MQL5 cap per CopyTicks call
#define MAX_REQUEST_TICKS        50000     // EA-side batching ceiling per request
#define RECONNECT_DELAY          3
#define HEARTBEAT_INTERVAL_MS    5000      // idle heartbeat cadence
#define MIN_CHUNK_MS             60000     // 1 minute — floor on adaptive shrink
#define PROGRESS_MIN_GAP_MS      200       // throttle progress messages to ~5 Hz
#define ONE_DAY_MS               86400000  // tick-sync chunk size
#define SYNC_PER_DAY_TIMEOUT_MS  30000     // max retry window per day on 4403

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

// ---- Pending SYNC_TICKS_RANGE state ----
bool   g_sync_active        = false;
string g_sync_id            = "";
string g_sync_symbol        = "";
long   g_sync_from_msc      = 0;
long   g_sync_to_msc        = 0;
long   g_sync_current_msc   = 0;
int    g_sync_total_days    = 0;
int    g_sync_days_synced   = 0;
int    g_sync_days_skipped  = 0;
uint   g_sync_day_start_ms  = 0;
uint   g_sync_last_prog_ms  = 0;
uint   g_sync_day_attempts  = 0;

//+------------------------------------------------------------------+
int OnInit()
{
   Print(">>> Backs2TickBridge v1.18: OnInit ENTRY");
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
   // liveness signal during a long running operation.
   if(g_pending_active)
      ProcessPendingChunk();

   if(g_sync_active)
      ProcessPendingSyncChunk();

   // Idle heartbeat. Suppressed while any long-running task is active
   // because PROGRESS already provides the liveness signal.
   if(!g_pending_active && !g_sync_active)
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
   if(g_pending_active)
   {
      Print(">>> Backs2TickBridge: aborting pending chunked request (id=",
            g_pending_id, ") due to disconnect");
      g_pending_active = false;
   }
   if(g_sync_active)
   {
      Print(">>> Backs2TickBridge: aborting pending sync request (id=",
            g_sync_id, ") due to disconnect");
      g_sync_active = false;
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

   Print(">>> Backs2TickBridge: REQ id=", id, " action=", action,
         " (", StringLen(json), " chars)");

   if(action == "PING")
      SendResponse(id, "\"status\":\"OK\"");
   else if(action == "GET_TICKS")
      HandleGetTicks(id, json);
   else if(action == "COUNT_TICKS")
      HandleCountTicks(id, json);
   else if(action == "SYNC_TICKS_RANGE")
      HandleSyncTicksRange(id, json);
   else if(action == "PREWARM")
      HandlePrewarm(id, json);
   else if(action == "GET_TERMINAL_INFO")
      HandleTerminalInfo(id);
   else
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Unknown action: " + action + "\"");
}

//+------------------------------------------------------------------+
// Serialization uses a uchar[] byte buffer. MQL5's string concat
// primitives (both `+=` and StringAdd) reallocate on every append —
// measured O(N²): 20k ticks took 40 s. Writing into a pre-sized byte
// buffer and SocketSend'ing it directly is O(N): same 20k ticks in ~14 ms.
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
//   Monolith mode (chunk_ms absent or 0): one CopyTicksRange call,
//   one response. Blocks OnTimer for the duration of the call.
//
//   Chunked mode (chunk_ms > 0): splits [from_msc, to_msc) into
//   ceil(span/chunk_ms) pieces. Each OnTimer tick processes exactly
//   one piece, emitting PROGRESS between them.
//
//   Latest-wins: a new request pre-empts an in-flight one.
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

   // Chunked mode. Latest-wins: pre-empt any in-flight long task.
   if(g_pending_active)
   {
      Print(">>> Backs2TickBridge: pre-empting in-flight COUNT_TICKS id=",
            g_pending_id, " with new id=", id);
      g_pending_active = false;
   }
   if(g_sync_active)
   {
      Print(">>> Backs2TickBridge: pre-empting in-flight SYNC_TICKS_RANGE id=",
            g_sync_id, " with new COUNT_TICKS id=", id);
      g_sync_active = false;
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
}

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
      if(g_pending_chunk_ms > MIN_CHUNK_MS)
      {
         long newChunk = g_pending_chunk_ms / 2;
         if(newChunk < MIN_CHUNK_MS) newChunk = MIN_CHUNK_MS;
         Print(">>> Backs2TickBridge: chunk failed (count=", count,
               " err=", err, ") — shrinking chunk_ms ",
               IntegerToString(g_pending_chunk_ms), " → ",
               IntegerToString(newChunk));
         g_pending_chunk_ms = newChunk;

         long remainingMs = g_pending_to_msc - g_pending_current_msc;
         int remainingChunks = (int)((remainingMs + g_pending_chunk_ms - 1) / g_pending_chunk_ms);
         g_pending_total_chunks = g_pending_chunks_done + remainingChunks;
         return;
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

   Print(">>> Backs2TickBridge: PROGRESS(COUNT) id=", g_pending_id,
         " chunk ", g_pending_chunks_done, "/", g_pending_total_chunks,
         " cumulative=", g_pending_cumulative);

   uchar buf[];
   int len = StringToCharArray(msg, buf) - 1;
   if(len <= 0) return;
   if(g_socket == INVALID_HANDLE || !SocketIsConnected(g_socket)) return;
   SocketSend(g_socket, buf, (uint)len);
}

//+------------------------------------------------------------------+
// SYNC_TICKS_RANGE (v1.17, ack added in v1.18):
//   Forces the terminal to download a specific tick range from the
//   broker and write it into the local .tkc cache. Iterates 1-day
//   chunks using CopyTicksRange(COPY_TICKS_ALL). On
//   ERR_HISTORY_TIMEOUT (4403) with zero ticks, retries the same day
//   on the next OnTimer tick (10 ms) up to SYNC_PER_DAY_TIMEOUT_MS.
//   Any non-timeout return — including a successful zero-tick
//   response — advances to the next day. Emits PROGRESS between days.
//
//   Latest-wins: a new chunked request pre-empts an in-flight one.
//+------------------------------------------------------------------+
void HandleSyncTicksRange(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   long   toMsc   = JsonGetLong(json, "to_msc");

   Print(">>> Backs2TickBridge: SYNC_TICKS_RANGE REQ id=", id,
         " symbol='", symbol, "' from=", IntegerToString(fromMsc),
         " to=", IntegerToString(toMsc));

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

   if(g_pending_active)
   {
      Print(">>> Backs2TickBridge: pre-empting in-flight COUNT_TICKS id=",
            g_pending_id, " with SYNC_TICKS_RANGE id=", id);
      g_pending_active = false;
   }
   if(g_sync_active)
   {
      Print(">>> Backs2TickBridge: pre-empting in-flight SYNC_TICKS_RANGE id=",
            g_sync_id, " with new id=", id);
      g_sync_active = false;
   }

   g_sync_active        = true;
   g_sync_id            = id;
   g_sync_symbol        = symbol;
   g_sync_from_msc      = fromMsc;
   g_sync_to_msc        = toMsc;
   g_sync_current_msc   = fromMsc;
   g_sync_total_days    = (int)((toMsc - fromMsc + ONE_DAY_MS - 1) / ONE_DAY_MS);
   g_sync_days_synced   = 0;
   g_sync_days_skipped  = 0;
   g_sync_day_start_ms  = GetTickCount();
   g_sync_last_prog_ms  = 0;   // bypass throttle for the initial ack
   g_sync_day_attempts  = 0;

   // Immediate ack: the first CopyTicksRange call may block for minutes
   // while the terminal downloads history from the broker. This PROGRESS
   // frame tells the C# side the request landed before that block starts.
   EmitSyncProgress();

   Print(">>> Backs2TickBridge: SYNC_TICKS_RANGE id=", id, " starting — ",
         IntegerToString(g_sync_total_days), " day(s) from ",
         IntegerToString(fromMsc), " to ", IntegerToString(toMsc));
}

//+------------------------------------------------------------------+
void ProcessPendingSyncChunk()
{
   if(!g_sync_active) return;

   if(!TerminalInfoInteger(TERMINAL_CONNECTED))
   {
      SendResponse(g_sync_id,
         "\"status\":\"ERROR\",\"message\":\"Terminal disconnected\"");
      Print(">>> Backs2TickBridge: SYNC_TICKS_RANGE id=", g_sync_id,
            " aborted — terminal disconnected");
      g_sync_active = false;
      return;
   }

   long dayFrom = g_sync_current_msc;
   long dayTo   = dayFrom + ONE_DAY_MS;
   if(dayTo > g_sync_to_msc) dayTo = g_sync_to_msc;

   g_sync_day_attempts++;

   MqlTick ticks[];
   int count = CopyTicksRange(g_sync_symbol, ticks, COPY_TICKS_ALL,
                              (ulong)dayFrom, (ulong)dayTo);
   int err = GetLastError();
   uint nowMs = GetTickCount();

   Print(">>> Backs2TickBridge: SYNC day [", IntegerToString(dayFrom),
         "..", IntegerToString(dayTo), ") attempt=", g_sync_day_attempts,
         " → count=", count, " err=", err);

   if(count == 0 && err == 4403)
   {
      // Terminal is still downloading this day's range from the broker.
      // Retry on the next OnTimer tick, up to the per-day timeout.
      if(nowMs - g_sync_day_start_ms > SYNC_PER_DAY_TIMEOUT_MS)
      {
         g_sync_days_skipped++;
         g_sync_current_msc = dayTo;
         g_sync_day_start_ms = nowMs;
         g_sync_day_attempts = 0;
         Print(">>> Backs2TickBridge: sync day [", IntegerToString(dayFrom),
               "..", IntegerToString(dayTo), ") timed out — skipping");
         // Fall through to the final-check below.
      }
      else
      {
         EmitSyncProgress();
         return;
      }
   }
   else
   {
      // Any non-timeout return means the terminal has answered for this
      // day: either a successful download (count >= 0) or a hard error
      // (count < 0). Both advance the cursor.
      if(count < 0)
         g_sync_days_skipped++;
      else
         g_sync_days_synced++;

      g_sync_current_msc = dayTo;
      g_sync_day_start_ms = nowMs;
      g_sync_day_attempts = 0;
   }

   if(g_sync_current_msc >= g_sync_to_msc)
   {
      string body = "\"status\":\"OK\",\"synced_days\":";
      body += IntegerToString(g_sync_days_synced);
      body += ",\"skipped_days\":";
      body += IntegerToString(g_sync_days_skipped);
      body += ",\"total_days\":";
      body += IntegerToString(g_sync_total_days);
      SendResponse(g_sync_id, body);

      Print(">>> Backs2TickBridge: SYNC_TICKS_RANGE id=", g_sync_id,
            " done — ", IntegerToString(g_sync_days_synced), " synced, ",
            IntegerToString(g_sync_days_skipped), " skipped");
      g_sync_active = false;
      return;
   }

   EmitSyncProgress();
}

//+------------------------------------------------------------------+
void EmitSyncProgress()
{
   uint nowMs = GetTickCount();
   if(nowMs - g_sync_last_prog_ms < PROGRESS_MIN_GAP_MS) return;
   g_sync_last_prog_ms = nowMs;

   int processed = g_sync_days_synced + g_sync_days_skipped;

   string msg = "{\"id\":0,\"action\":\"PROGRESS\",\"request_id\":";
   msg += g_sync_id;
   msg += ",\"chunks_done\":";
   msg += IntegerToString(processed);
   msg += ",\"chunks_total\":";
   msg += IntegerToString(g_sync_total_days);
   msg += ",\"synced_days\":";
   msg += IntegerToString(g_sync_days_synced);
   msg += ",\"skipped_days\":";
   msg += IntegerToString(g_sync_days_skipped);
   msg += "}\n";

   Print(">>> Backs2TickBridge: PROGRESS(SYNC) id=", g_sync_id,
         " processed=", processed, "/", g_sync_total_days,
         " synced=", g_sync_days_synced, " skipped=", g_sync_days_skipped);

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
// v1.12 tracks the trailing same-ms count on each slice and skips
// exactly that many leading ticks on the next slice.
//
// v1.14: the "ticks" array body is a flat numeric sequence, 7 numbers
// per tick: time_msc, bid, ask, last, volume, volume_real, flags.
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
   Print(">>> Backs2TickBridge: RESP id=", id, " (", len, " bytes)");
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