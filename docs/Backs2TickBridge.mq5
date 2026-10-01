//+------------------------------------------------------------------+
//|                                            Backs2TickBridge.mq5  |
//|                    MT5 raw tick data TCP client (EA, not script) |
//|                                                                  |
//|  NOTE: BridgeHost/BridgePort must match AppSettings.Mt5Host and  |
//|  AppSettings.Mt5Port in backs2's config/settings.json. Edit both  |
//|  sides if you change the port.                                    |
//+------------------------------------------------------------------+
#property copyright "backs2"
#property version   "1.13"
#property description "Connects to the backs2 C# server and serves raw MqlTick data"
#property strict

// ---- Bootstrap (hardcoded, no inputs) ----
#define BRIDGE_HOST         "127.0.0.1"
#define BRIDGE_PORT         5555
#define BRIDGE_TIMEOUT_MS   5000
#define COPYTICKS_CAP       2000      // hard MQL5 cap per CopyTicks call
#define MAX_REQUEST_TICKS   50000     // EA-side batching ceiling per request
#define RECONNECT_DELAY     3

int    g_socket    = INVALID_HANDLE;
string g_readBuffer = "";
datetime g_lastReconnectAttempt = 0;
bool   g_timerLogged = false;

//+------------------------------------------------------------------+
int OnInit()
{
   Print(">>> Backs2TickBridge v1.13: OnInit ENTRY");
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
// COUNT_TICKS (v1.13):
//   Ground-truth verification. Uses CopyTicksRange — the same data
//   the C# fetch reads from, but queried directly by the terminal.
//   Returns the total tick count in [from_msc, to_msc), plus the
//   actual first/last timestamps so the caller can see how far the
//   returned range sits inside the requested one.
//
//   Payload is small (<200 bytes), so the string-concat path used by
//   SendResponse is fine here — no need for the byte-buffer hot path.
//+------------------------------------------------------------------+
void HandleCountTicks(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   long   toMsc   = JsonGetLong(json, "to_msc");

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

   int cap = total * 200 + 512;
   uchar buf[];
   ArrayResize(buf, cap);
   int pos = 0;

   AppendStr(buf, pos, "{\"id\":");
   AppendStr(buf, pos, id);
   AppendStr(buf, pos, ",\"status\":\"OK\",\"count\":");
   AppendStr(buf, pos, IntegerToString(total));
   AppendStr(buf, pos, ",\"next_from_msc\":");
   AppendStr(buf, pos, IntegerToString(lastEmittedTimeMsc));
   AppendStr(buf, pos, ",\"perf_copy_us\":");
   AppendStr(buf, pos, IntegerToString((long)perfCopyUs));
   AppendStr(buf, pos, ",\"ticks\":[");

   ulong serStart = GetMicrosecondCount();

   for(int i = 0; i < total; i++)
   {
      if(i > 0) AppendStr(buf, pos, ",");
      AppendStr(buf, pos, "{\"time_msc\":");
      AppendStr(buf, pos, IntegerToString((long)collected[i].time_msc));
      AppendStr(buf, pos, ",\"bid\":");
      AppendStr(buf, pos, DoubleToString(collected[i].bid, 8));
      AppendStr(buf, pos, ",\"ask\":");
      AppendStr(buf, pos, DoubleToString(collected[i].ask, 8));
      AppendStr(buf, pos, ",\"last\":");
      AppendStr(buf, pos, DoubleToString(collected[i].last, 8));
      AppendStr(buf, pos, ",\"volume\":");
      AppendStr(buf, pos, IntegerToString((long)collected[i].volume));
      AppendStr(buf, pos, ",\"volume_real\":");
      AppendStr(buf, pos, DoubleToString(collected[i].volume_real, 8));
      AppendStr(buf, pos, ",\"flags\":");
      AppendStr(buf, pos, IntegerToString((int)collected[i].flags));
      AppendStr(buf, pos, "}");
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