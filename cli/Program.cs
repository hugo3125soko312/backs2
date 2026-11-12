using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.WalkForward;
using backs2.Core.Optimizer;
using backs2.Core.Accounts;

namespace backs2.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("backs2 - Backtesting & Walk-Forward Optimization CLI");

        // Walk-Forward Command
        var wfCommand = new Command("walkforward", "Run walk-forward optimization");
        var wfBatchId = new Option<string?>(["--batch-id", "-b"], "Specific batch ID to process");
        var wfOutput = new Option<string?>(["--output", "-o"], "Output JSON file path");
        wfCommand.AddOption(wfBatchId);
        wfCommand.AddOption(wfOutput);
        wfCommand.SetHandler(async (batchId, output) =>
        {
            var store = new WalkForwardStore();
            var batches = store.ListBatchFiles();

            if (!string.IsNullOrEmpty(batchId))
            {
                batches = batches.Where(f => Path.GetFileNameWithoutExtension(f) == batchId).ToList();
            }

            var results = new List<WalkForwardSummary>();
            foreach (var file in batches)
            {
                var batch = await store.LoadAsync(file, CancellationToken.None);
                if (batch == null) continue;

                var windows = batch.Windows.Select(w => new WalkForwardWindowRow(batch, w)).ToList();
                var aggregator = new WalkForwardAggregator();
                var summary = aggregator.Aggregate(windows);
                results.Add(summary);
            }

            var outputPath = output ?? "walkforward-results.json";
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(results, options));
            Console.WriteLine($"Results written to {outputPath}");
        }, wfBatchId, wfOutput);
        rootCommand.AddCommand(wfCommand);

        // Monte Carlo Command
        var mcCommand = new Command("montecarlo", "Run Monte Carlo simulation");
        var mcInput = new Option<string?>(["--input", "-i"], "Input file path");
        var mcOutput = new Option<string?>(["--output", "-o"], "Output JSON file path");
        mcCommand.AddOption(mcInput);
        mcCommand.AddOption(mcOutput);
        mcCommand.SetHandler(async (input, output) =>
        {
            if (string.IsNullOrEmpty(input) || !File.Exists(input))
            {
                Console.Error.WriteLine("Input file not found: " + input);
                return;
            }

            var json = await File.ReadAllTextAsync(input);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<MonteCarloInputData>(json, options);

            if (data == null)
            {
                Console.Error.WriteLine("Failed to deserialize input");
                return;
            }

            // Run Monte Carlo (simplified - in real code, use ComputeSharp)
            var result = new MonteCarloResult
            {
                Iterations = data.Iterations,
                WinRate = 0.5,
                ExpectedReturn = data.InitialCapital * 0.1
            };

            var outputPath = output ?? "montecarlo-results.json";
            var outputOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, outputOptions));
            Console.WriteLine($"Monte Carlo results written to {outputPath}");
        }, mcInput, mcOutput);
        rootCommand.AddCommand(mcCommand);

        // Optimize Command
        var optCommand = new Command("optimize", "Run parameter optimization");
        var optStrategy = new Option<string?>(["--strategy", "-s"], "Strategy name");
        var optDataset = new Option<string?>(["--dataset", "-d"], "Dataset identifier");
        var optOutput = new Option<string?>(["--output", "-o"], "Output JSON file path");
        optCommand.AddOption(optStrategy);
        optCommand.AddOption(optDataset);
        optCommand.AddOption(optOutput);
        optCommand.SetHandler((strategy, dataset, output) =>
        {
            Console.WriteLine($"Optimizing strategy '{strategy}' on dataset '{dataset}'");

            // Simplified - real implementation would use OptimizationRunner
            var result = new OptimizationResult
            {
                Strategy = strategy ?? "Unknown",
                Dataset = dataset ?? "Unknown",
                Parameters = new[] { new ParameterResult { Name = "test", Value = 1.0 } }
            };

            var outputPath = output ?? "optimization-results.json";
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(result, options));
            Console.WriteLine($"Optimization results written to {outputPath}");
        }, optStrategy, optDataset, optOutput);
        rootCommand.AddCommand(optCommand);

        // Export Command
        var exportCommand = new Command("export", "Export data to various formats");
        var exportType = new Option<string>(["--type", "-t"], () => "json", "Export type: json, csv");
        var exportInput = new Option<string?>(["--input", "-i"], "Input file path");
        var exportOutput = new Option<string?>(["--output", "-o"], "Output file path");
        exportCommand.AddOption(exportType);
        exportCommand.AddOption(exportInput);
        exportCommand.AddOption(exportOutput);
        exportCommand.SetHandler((type, input, output) =>
        {
            if (string.IsNullOrEmpty(input))
            {
                Console.Error.WriteLine("Input file required");
                return;
            }

            Console.WriteLine($"Exporting {type} from {input} to {output ?? input + ".export"}");

            // Simplified export logic
            if (!File.Exists(input))
            {
                Console.Error.WriteLine("Input file not found: " + input);
                return;
            }

            var data = File.ReadAllText(input);
            File.WriteAllText(output ?? input + ".export", data);
            Console.WriteLine("Export complete");
        }, exportType, exportInput, exportOutput);
        rootCommand.AddCommand(exportCommand);

        return await rootCommand.InvokeAsync(args);
    }

    // Data classes for JSON input/output
    public class MonteCarloInputData
    {
        public int Iterations { get; set; }
        public double InitialCapital { get; set; }
        public double RiskPerTrade { get; set; }
        public int Seed { get; set; }
    }

    public class OptimizationResult
    {
        public string? Strategy { get; set; }
        public string? Dataset { get; set; }
        public ParameterResult[]? Parameters { get; set; }
        public double ObjectiveValue { get; set; }
    }

    public class ParameterResult
    {
        public string? Name { get; set; }
        public double Value { get; set; }
    }

    public class MonteCarloResult
    {
        public int Iterations { get; set; }
        public double WinRate { get; set; }
        public double ExpectedReturn { get; set; }
        public double SharpeRatio { get; set; }
        public double MaxDrawdown { get; set; }
    }
}
