// src/core/Runner/TradeRecorder.cs
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
    private double _openPrice;

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

    public void Enter(string side, double price)
    {
        if (_hasOpen) return;
        _hasOpen = true;
        _openSide = side;
        _openPrice = price;
        _openTime = CurrentTime();
    }

    public void Exit(double price)
    {
        if (!_hasOpen) return;

        var exitTime = CurrentTime();
        double rawPnl = _openSide == "Long"
            ? price - _openPrice
            : _openPrice - price;

        double pct = _openPrice != 0 ? rawPnl / _openPrice : 0;

        _closed.Add(new Trade(
            _openTime,
            exitTime,
            _openPrice,
            price,
            _openSide,
            rawPnl,
            pct));

        _hasOpen = false;
    }

    private DateTime CurrentTime()
        => _cursor >= 0 && _cursor < _data.Count
            ? _data[_cursor].Timestamp
            : DateTime.UtcNow;
}