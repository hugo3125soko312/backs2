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