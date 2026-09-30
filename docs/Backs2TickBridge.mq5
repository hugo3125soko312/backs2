//+------------------------------------------------------------------+
//|                                            Backs2TickBridge.mq5  |
//|                    MT5 raw tick data TCP client (EA, not script) |
//|                                                                  |
//|  NOTE: BridgeHost/BridgePort must match AppSettings.Mt5Host and  |
//|  AppSettings.Mt5Port in backs2's config/settings.json. Edit both |
//|  sides if you change the port.                                    |
//+------------------------------------------------------------------+
#property copyright "backs2"
#property version   "1.06"
#property description "Connects to the backs2 C# server and serves raw MqlTick data"
#property strict

// ---- Bootstrap (hardcoded, no inputs) ----
#define BRIDGE_HOST       "127.0.0.1"
#define BRIDGE_PORT       5555
#define BRIDGE_TIMEOUT_MS 5000
#define COPYTICKS_CAP     2000
#define RECONNECT_DELAY   3

int    g_socket    = INVALID_HANDLE;
string g_readBuffer = "";
datetime g_lastReconnectAttempt = 0;
bool   g_timerLogged = false;

//+------------------------------------------------------------------+
int OnInit()
{
   Print(">>> Backs2TickBridge v1.06: OnInit ENTRY");
   if(!ConnectToServer())
      Print(">>> Backs2TickBridge: initial connection failed, will retry in OnTimer");
   else
      Print(">>> Backs2TickBridge: initial connection OK");
   EventSetMillisecondTimer(200);
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

   uint available = SocketIsReadable(g_socket);
   if(available == 0) return;

   uchar buf[];
   int bytesRead = SocketRead(g_socket, buf, available, 100);
   if(bytesRead <= 0) return;

   g_readBuffer += CharArrayToString(buf, 0, bytesRead);

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
   else if(action == "GET_TERMINAL_INFO")
      HandleTerminalInfo(id);
   else
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Unknown action: " + action + "\"");
}

//+------------------------------------------------------------------+
void HandleGetTicks(string id, string json)
{
   string symbol  = JsonGetString(json, "symbol");
   long   fromMsc = JsonGetLong(json, "from_msc");
   int    count   = (int)JsonGetLong(json, "count");

   if(symbol == "")
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"Missing symbol\"");
      return;
   }

   if(count <= 0)          count = 200;
   if(count > COPYTICKS_CAP) count = COPYTICKS_CAP;

   ulong resolvedFrom = (fromMsc > 0)
      ? (ulong)fromMsc
      : (ulong)(TimeCurrent() - 86400) * 1000;   // default: last 24h

   MqlTick ticks[];
   int copied = CopyTicks(symbol, ticks, COPY_TICKS_ALL, resolvedFrom, (uint)count);
   int copyErr = GetLastError();

   if(copied <= 0)
   {
      SendResponse(id, "\"status\":\"ERROR\",\"message\":\"CopyTicks returned " +
                   IntegerToString(copied) + ", error " + IntegerToString(copyErr) + "\"");
      return;
   }

   long lastMsc = (long)ticks[copied - 1].time_msc;

   string body = "\"status\":\"OK\",\"count\":" + IntegerToString(copied);
   body += ",\"next_from_msc\":" + IntegerToString(lastMsc + 1);
   body += ",\"ticks\":[";

   for(int i = 0; i < copied; i++)
   {
      if(i > 0) body += ",";
      body += "{\"time_msc\":"    + IntegerToString((long)ticks[i].time_msc);
      body += ",\"bid\":"         + DoubleToString(ticks[i].bid, 8);
      body += ",\"ask\":"         + DoubleToString(ticks[i].ask, 8);
      body += ",\"last\":"        + DoubleToString(ticks[i].last, 8);
      body += ",\"volume\":"      + IntegerToString((long)ticks[i].volume);
      body += ",\"volume_real\":" + DoubleToString(ticks[i].volume_real, 8);
      body += ",\"flags\":"       + IntegerToString((int)ticks[i].flags);
      body += "}";
   }
   body += "]";
   SendResponse(id, body);
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