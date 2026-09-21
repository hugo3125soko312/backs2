using System;

namespace backs2.Core.Models;

public record Kline(
    DateTime Timestamp,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume
);

// Disk format: timestamp as Unix ms (matches Massive's wire format),
// compact field names to reduce file size.
public record KlineCompact(
    long T,
    double O,
    double H,
    double L,
    double C,
    long V
);