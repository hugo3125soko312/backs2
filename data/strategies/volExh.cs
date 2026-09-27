// @param VolSmaLen int = 10
// @param VolMult double = 1.8
// @param MaxBodyPct double = 0.6
// @param LookbackBars int = 2
// @param RrTarget double = 2.7
// @param SlMultiplier double = 1.7

var culture = System.Globalization.CultureInfo.InvariantCulture;
int volSmaLen = int.TryParse(Params["VolSmaLen"], out var vsl) ? vsl : 10;
double volMult = double.TryParse(Params["VolMult"], System.Globalization.NumberStyles.Any, culture, out var vm) ? vm : 1.8;
double maxBodyPct = double.TryParse(Params["MaxBodyPct"], System.Globalization.NumberStyles.Any, culture, out var mbp) ? mbp : 0.6;
int lookbackBars = int.TryParse(Params["LookbackBars"], out var lb) ? lb : 2;
double rrTarget = double.TryParse(Params["RrTarget"], System.Globalization.NumberStyles.Any, culture, out var rr) ? rr : 2.7;
double slMultiplier = double.TryParse(Params["SlMultiplier"], System.Globalization.NumberStyles.Any, culture, out var slm) ? slm : 1.7;

bool inTrade = false;
double slPrice = 0.0;
double tpPrice = 0.0;

// Pre-calculate Volume SMA 
double[] volMAs = new double[Data.Count];
double currentVolSum = 0;
for (int i = 0; i < Data.Count; i++)
{
    currentVolSum += Data[i].Volume;
    if (i >= volSmaLen)
    {
        currentVolSum -= Data[i - volSmaLen].Volume;
    }
    volMAs[i] = i >= volSmaLen - 1 ? currentVolSum / volSmaLen : Data[i].Volume;
}

for (int i = 0; i < Data.Count; i++)
{
    Trades.Advance(i);
    var kline = Data[i];

    if (inTrade)
    {
        if (kline.Low <= slPrice)
        {
            Trades.Exit(slPrice);
            inTrade = false;
        }
        else if (kline.High >= tpPrice)
        {
            Trades.Exit(tpPrice);
            inTrade = false;
        }
        continue;
    }

    if (i < volSmaLen + lookbackBars) continue;

    // Check for downward volume spike in the lookback window
    bool downwardVolSpike = false;
    for (int j = 1; j <= lookbackBars; j++)
    {
        int prevIdx = i - j;
        var prevKline = Data[prevIdx];
        
        bool prevIsDown = prevKline.Close < prevKline.Open;
        bool prevVolSpike = prevKline.Volume > (volMAs[prevIdx] * volMult);

        if (prevIsDown && prevVolSpike)
        {
            downwardVolSpike = true;
            break;
        }
    }

    // Check current candle criteria
    double candleRange = kline.High - kline.Low;
    double bodySize = kline.Close - kline.Open;
    bool isBullish = kline.Close > kline.Open;
    bool isSlightBullish = isBullish && (candleRange > 0 ? (bodySize / candleRange) <= maxBodyPct : false);

    if (downwardVolSpike && isSlightBullish)
    {
        double riskAmount = System.Math.Max(candleRange * slMultiplier, 0.00001);
        double entryPrice = kline.Close;
        
        slPrice = entryPrice - riskAmount;
        tpPrice = entryPrice + (riskAmount * rrTarget);

        Trades.Enter("Long", entryPrice);
        inTrade = true;
    }
}