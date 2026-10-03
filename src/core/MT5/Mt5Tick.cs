namespace backs2.Core.MT5;

public readonly record struct Mt5Tick(
    long TimeMsc,
    double Bid,
    double Ask,
    double Last,
    ulong Volume,
    double VolumeReal,
    uint Flags);