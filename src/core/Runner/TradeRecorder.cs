using System;
using System.Collections.Generic;
using backs2.Core.Models;

namespace backs2.Core.Runner;

public sealed class TradeRecorder
{
    private readonly List<Trade> _closed = new();
    private readonly List<Kline> _data;
    private int _cursor;

    private bool _hasOpen;
    private string _openSide = "";
    private DateTime _openTime;
    private int _openIndex;
    private double _openPrice;
    private double _openSize = 1.0;

    public TradeRecorder(List<Kline> data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public IReadOnlyList<Trade> Closed => _closed;

    public void Advance(int index)
    {
        if (index >= 0 && index < _data.Count)
            _cursor = index;
    }

    public void Enter(string side, double price, double size = 1.0)
    {
        if (_hasOpen) return;
        if (size <= 0) size = 1.0;

        _hasOpen = true;
        _openSide = side;
        _openPrice = price;
        _openSize = size;
        _openTime = CurrentTime();
        _openIndex = _cursor;
    }

    public void Exit(double price)
    {
        if (!_hasOpen) return;

        var exitTime = CurrentTime();
        int exitIndex = _cursor;

        double rawPnl = _openSide == "Long"
            ? (price - _openPrice) * _openSize
            : (_openPrice - price) * _openSize;

        double pct = _openPrice != 0
            ? (_openSide == "Long"
                ? (price - _openPrice) / _openPrice
                : (_openPrice - price) / _openPrice)
            : 0;

        _closed.Add(new Trade(
            EntryTime: _openTime,
            ExitTime: exitTime,
            EntryIndex: _openIndex,
            ExitIndex: exitIndex,
            EntryPrice: _openPrice,
            ExitPrice: price,
            Size: _openSize,
            Side: _openSide,
            PnL: rawPnl,
            PnLPercent: pct));

        _hasOpen = false;
    }

    private DateTime CurrentTime()
        => _cursor >= 0 && _cursor < _data.Count
            ? _data[_cursor].Timestamp
            : DateTime.UtcNow;
}