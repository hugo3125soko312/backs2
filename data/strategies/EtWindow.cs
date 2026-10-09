// @param Side string = "Long" options: "Long", "Short"
// @param StartMinute int = 540
// @param EndMinute int = 570

using System;
using System.Globalization;
using backs2.Core.Runner;
using backs2.Core.Utils;

string side = Params["Side"];

int startMinutes = int.TryParse(Params["StartMinute"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sm)
    ? sm : 9 * 60;
int endMinutes = int.TryParse(Params["EndMinute"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var em)
    ? em : 9 * 60 + 30;

if (startMinutes < 0 || startMinutes > 24 * 60 || endMinutes < 0 || endMinutes > 24 * 60)
{
    Log($"Window bounds out of range (start={startMinutes}, end={endMinutes}) - no trades taken.");
    return;
}
if (endMinutes <= startMinutes)
{
    Log($"Window invalid (start={startMinutes}, end={endMinutes}) - no trades taken.");
    return;
}

if (Data.Count == 0) return;

bool inTrade = false;
DateTime? entryDay = null;

for (int i = 0; i < Data.Count; i++)
{
    Trades.Advance(i);
    var k = Data[i];

    var et = EasternTime.ToEastern(k.Timestamp);
    int minuteOfDay = et.Hour * 60 + et.Minute;

    if (inTrade)
    {
        bool sameDay = entryDay.HasValue && et.Date == entryDay.Value;
        if (!sameDay || minuteOfDay >= endMinutes)
        {
            Trades.Exit(k.Open);
            inTrade = false;
        }
        continue;
    }

    if (entryDay.HasValue && et.Date == entryDay.Value) continue;
    if (minuteOfDay < startMinutes) continue;
    if (minuteOfDay >= endMinutes) continue;

    Trades.Enter(side, k.Open);
    inTrade = true;
    entryDay = et.Date;
}

if (inTrade && Data.Count > 0)
{
    Trades.Advance(Data.Count - 1);
    Trades.Exit(Data[Data.Count - 1].Close);
}