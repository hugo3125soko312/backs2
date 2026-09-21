var vwap = Engine.GetMetric("VWAP").Calculate(Data);
Log($"HelloVwap: bars={Data.Count} vwap={vwap:F2} last={Data[^1].Close:F2}");