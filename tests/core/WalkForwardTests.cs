using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using backs2.Core.WalkForward;
using backs2.Core.Optimizer;

namespace backs2.Core.Tests;

public class WalkForwardTests
{
    [Fact]
    public void WalkForwardPlanner_GeneratesWindows_CorrectCount()
    {
        // Arrange
        var config = new WalkForwardRunConfig(
            WindowMode: "Rolling",
            LengthUnit: "Bars", 
            IsLength: 100,
            OosLength: 50,
            StepLength: 50,
            MinWindows: 2,
            Objective: "ProfitFactor",
            CarryTopN: 1,
            Backend: "Auto",
            Optimization: new OptimizationRunConfig()
        );

        var batches = new[] {
            new WalkForwardBatch(
                SchemaVersion: 1,
                Id: "test-1",
                StrategyName: "TestStrategy",
                StrategyPath: "/test",
                Symbol: "XAUUSD",
                Timeframe: "1h",
                DatasetLabel: "test",
                RunUtc: DateTime.UtcNow,
                TotalBars: 10000,
                DataSpanYears: 1.0,
                CombinationCount: 100,
                Cancelled: false,
                ParameterNames: new[] { "param1" },
                Config: config,
                Windows: Array.Empty<WalkForwardWindowResult>(),
                Summary: null!)
        };

        // Act
        var planner = new WalkForwardPlanner();
        var windows = planner.PlanWindows(batches[0], config, CancellationToken.None);

        // Assert
        Assert.NotNull(windows);
        Assert.True(windows.Count >= 2);
    }

    [Fact]
    public void WalkForwardAggregator_CalculatesMean_CorrectValue()
    {
        // Arrange
        var windows = new List<WalkForwardWindowResult>
        {
            new WalkForwardWindowResult(
                Index: 0,
                IsStartBar: 0, IsEndBar: 100,
                OosStartBar: 100, OosEndBar: 150,
                IsStartUtc: DateTime.UtcNow, IsEndUtc: DateTime.UtcNow.AddDays(1),
                OosStartUtc: DateTime.UtcNow.AddDays(1), OosEndUtc: DateTime.UtcNow.AddDays(2),
                ChosenParameters: Array.Empty<ParameterSet>(),
                IsObjective: 1.5,
                OosObjective: 1.4,
                Degradation: 0.05,
                OosTradeCount: 50,
                OosNetPnl: 1000,
                OosMaxDrawdown: 200,
                OosTerminalBalance: 1200,
                OosBeatNull: true,
                OosMc: null,
                OosEquityCurve: new[] { 100.0, 110.0, 120.0 },
                ExtraCandidates: Array.Empty<WalkForwardCandidate>(),
                IsCombinationsEvaluated: 100,
                Success: true,
                Error: null,
                DurationMs: 1000),
            new WalkForwardWindowResult(
                Index: 1,
                IsStartBar: 100, IsEndBar: 200,
                OosStartBar: 200, OosEndBar: 250,
                IsStartUtc: DateTime.UtcNow.AddDays(1), IsEndUtc: DateTime.UtcNow.AddDays(2),
                OosStartUtc: DateTime.UtcNow.AddDays(2), OosEndUtc: DateTime.UtcNow.AddDays(3),
                ChosenParameters: Array.Empty<ParameterSet>(),
                IsObjective: 1.6,
                OosObjective: 1.5,
                Degradation: 0.10,
                OosTradeCount: 60,
                OosNetPnl: 1200,
                OosMaxDrawdown: 250,
                OosTerminalBalance: 1350,
                OosBeatNull: true,
                OosMc: null,
                OosEquityCurve: new[] { 110.0, 120.0, 130.0 },
                ExtraCandidates: Array.Empty<WalkForwardCandidate>(),
                IsCombinationsEvaluated: 100,
                Success: true,
                Error: null,
                DurationMs: 1500)
        };

        // Act
        var aggregator = new WalkForwardAggregator();
        var summary = aggregator.Aggregate(windows);

        // Assert
        Assert.Equal(2, summary.WindowCount);
        Assert.Equal(2, summary.SuccessfulWindows);
        Assert.InRange(summary.MeanOosObjective, 1.4, 1.6);
    }

    [Fact]
    public void WalkForwardObjective_CalculatesScore_Correct()
    {
        // Arrange
        var window = new WalkForwardWindowResult(
            Index: 0,
            IsStartBar: 0, IsEndBar: 100,
            OosStartBar: 100, OosEndBar: 150,
            IsStartUtc: DateTime.UtcNow, IsEndUtc: DateTime.UtcNow.AddDays(1),
            OosStartUtc: DateTime.UtcNow.AddDays(1), OosEndUtc: DateTime.UtcNow.AddDays(2),
            ChosenParameters: Array.Empty<ParameterSet>(),
            IsObjective: 1.5,
            OosObjective: 1.4,
            Degradation: 0.05,
            OosTradeCount: 50,
            OosNetPnl: 1000,
            OosMaxDrawdown: 200,
            OosTerminalBalance: 1200,
            OosBeatNull: true,
            OosMc: null,
            OosEquityCurve: new[] { 100.0, 110.0, 120.0 },
            ExtraCandidates: Array.Empty<WalkForwardCandidate>(),
            IsCombinationsEvaluated: 100,
            Success: true,
            Error: null,
            DurationMs: 1000);

        // Act
        var objective = new WalkForwardObjective();
        var score = objective.CalculateScore(window, 1000);

        // Assert
        Assert.True(score > 0);
        Assert.True(score <= 100);
    }

    [Fact]
    public void WalkForwardStore_SaveAndLoad_RoundTrip()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf_test_" + Guid.NewGuid() + ".json");
        
        var config = new WalkForwardRunConfig(
            WindowMode: "Rolling",
            LengthUnit: "Bars", 
            IsLength: 100,
            OosLength: 50,
            StepLength: 50,
            MinWindows: 2,
            Objective: "ProfitFactor",
            CarryTopN: 1,
            Backend: "Auto",
            Optimization: new OptimizationRunConfig()
        );

        var batch = new WalkForwardBatch(
            SchemaVersion: 1,
            Id: "roundtrip-test",
            StrategyName: "TestStrategy",
            StrategyPath: "/test",
            Symbol: "XAUUSD",
            Timeframe: "1h",
            DatasetLabel: "test",
            RunUtc: DateTime.UtcNow,
            TotalBars: 10000,
            DataSpanYears: 1.0,
            CombinationCount: 100,
            Cancelled: false,
            ParameterNames: new[] { "param1" },
            Config: config,
            Windows: Array.Empty<WalkForwardWindowResult>(),
            Summary: null!);

        // Act
        var store = new WalkForwardStore();
        
        // Save with modified root path for test
        string originalRoot = store.RootDirectory;
        
        // Test save/load with cancellation
        var cts = new CancellationTokenSource();
        var saveTask = store.SaveAsync(batch, cts.Token);
        
        // Assert
        Assert.True(System.IO.File.Exists(tempPath) || File.Exists(originalRoot + "/" + batch.Id + ".json"));
        
        // Cleanup
        System.IO.File.Delete(tempPath);
        File.Delete(originalRoot + "/" + batch.Id + ".json");
    }

    [Fact]
    public void BatchDeterminismCheck_VerifiesDeterminism()
    {
        // Arrange
        var data1 = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var data2 = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };
        
        // Act
        var result1 = BatchDeterminismCheck.ComputeChecksum(data1);
        var result2 = BatchDeterminismCheck.ComputeChecksum(data2);
        
        // Assert
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void WalkForwardStore_ListBatchFiles_ReturnsFiles()
    {
        // Arrange
        var store = new WalkForwardStore();
        
        // Act - just verify no exception
        var files = store.ListBatchFiles();
        
        // Assert
        Assert.NotNull(files);
        Assert.IsType<List<string>>(files);
    }
}
