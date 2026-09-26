using System.Runtime.InteropServices;

namespace backs2.Core.Overview;

[StructLayout(LayoutKind.Sequential)]
public readonly struct TimeSlice
{
    public readonly long Timestamp;
    public readonly double MtmHigh;
    public readonly double MtmLow;
    public readonly double RealizedPnl;
    public readonly byte IsEodReset;

    public TimeSlice(long timestamp, double mtmHigh, double mtmLow, double realizedPnl, byte isEodReset)
    {
        Timestamp = timestamp;
        MtmHigh = mtmHigh;
        MtmLow = mtmLow;
        RealizedPnl = realizedPnl;
        IsEodReset = isEodReset;
    }

    public bool IsEod => IsEodReset != 0;
}