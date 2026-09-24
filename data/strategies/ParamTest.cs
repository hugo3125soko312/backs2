// @param Entry_Threshold double = 1.5
// @param Exit_Threshold double = 0.5
// @param Use_Vwap_Filter bool = true
// @param Side string = "Long" options: "Long", "Short"
// @param Max_Trades int = 10

// ParamTest: exercises the @param parser, the Params dictionary injection,
// the preset round-trip, and the TradeRecorder. Logs each declared parameter
// value, then emits a synthetic trade each time VWAP crosses a threshold.

var vwap = Engine.GetMetric("VWAP");
var vwapValue = vwap.Calculate(Data);

Log($"ParamTest: bars={Data.Count} VWAP={vwapValue:F4}");
Log($"ParamTest: Entry_Threshold={Params["Entry_Threshold"]}");
Log($"ParamTest: Exit_Threshold={Params["Exit_Threshold"]}");
Log($"ParamTest: Use_Vwap_Filter={Params["Use_Vwap_Filter"]}");
Log($"ParamTest: Side={Params["Side"]}");
Log($"ParamTest: Max_Trades={Params["Max_Trades"]}");

bool useFilter = Params["Use_Vwap_Filter"] == "true";
string side = Params["Side"];
int maxTrades = int.TryParse(Params["Max_Trades"], out var mt) ? mt : 10;

int emitted = 0;
for (int i = 1; i < Data.Count && emitted < maxTrades; i++)
{
    Trades.Advance(i);

    var prev = Data[i - 1];
    var cur = Data[i];

    // Synthetic entry: green candle after red (or flipped for Short).
    bool longSignal = prev.Close < prev.Open && cur.Close > cur.Open;
    bool shortSignal = prev.Close > prev.Open && cur.Close < cur.Open;

    bool entry = side == "Long" ? longSignal : shortSignal;
    if (!entry) continue;

    if (useFilter && cur.Close < vwapValue) continue;

    Trades.Enter(side, cur.Close);

    // Exit at the next bar for simplicity.
    if (i + 1 < Data.Count)
    {
        Trades.Advance(i + 1);
        Trades.Exit(Data[i + 1].Close);
        emitted++;
    }
}

Log($"ParamTest: emitted {emitted} trades");