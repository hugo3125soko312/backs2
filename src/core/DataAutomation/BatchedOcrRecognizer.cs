using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using backs2.Core;

namespace backs2.Core.DataAutomation;

/// <summary>
/// Direct ONNX Runtime wrapper around the PP-OCRv5 mobile recognizer.
/// Skips detection entirely — the caller guarantees each crop is a single
/// horizontal text line. Accepts a batch of bitmaps and runs one forward
/// pass per batch, which is what makes GPU worthwhile: kernel launch and
/// PCIe transfer are amortized across N crops instead of paid per crop.
/// </summary>
public sealed class BatchedOcrRecognizer : IDisposable
{
    // PP-OCRv5 mobile recognizer constants.
    private const int ModelHeight = 48;
    private const int MaxWidth = 320;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _dict;

    public BatchedOcrRecognizer(string modelPath, string dictPath, bool useGpu)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Recognizer model not found.", modelPath);
        if (!File.Exists(dictPath))
            throw new FileNotFoundException("Recognizer dict not found.", dictPath);

        var opts = new SessionOptions();

        // The pool is driven by the outer batching loop, so keep intra-op
        // threads low — one session, one forward pass at a time.
        opts.IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 4);
        opts.InterOpNumThreads = 1;
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        if (useGpu)
        {
            try
            {
                opts.AppendExecutionProvider_CUDA();
                LogManager.Info(LogTag.Data, "BatchedOcrRecognizer: CUDA EP appended.");
            }
            catch (Exception ex)
            {
                LogManager.Warn(LogTag.Data,
                    $"BatchedOcrRecognizer: CUDA unavailable ({ex.Message}). Using CPU.");
                opts.AppendExecutionProvider_CPU();
            }
        }
        else
        {
            opts.AppendExecutionProvider_CPU();
        }

        _session = new InferenceSession(modelPath, opts);
        _inputName = _session.InputMetadata.Keys.First();
        _dict = File.ReadAllLines(dictPath);

        LogManager.Info(LogTag.Data,
            $"BatchedOcrRecognizer ready. Input={_inputName}, " +
            $"dictSize={_dict.Length}, " +
            $"inputs=[{string.Join(",", _session.InputMetadata.Select(kv => $"{kv.Key}:{string.Join("x", kv.Value.Dimensions)}"))}]");
    }

    /// <summary>
    /// Runs one forward pass over the crops. Returns one text string per crop
    /// in the same order. Crops are scaled to ModelHeight keeping aspect ratio,
    /// right-padded with zeros to the widest in the batch.
    /// </summary>
    public string[] RecognizeBatch(IReadOnlyList<SKBitmap?> crops)
    {
        int n = crops.Count;
        if (n == 0) return Array.Empty<string>();

        // ── Resize each crop ──────────────────────────────────────────
        var resized = new SKBitmap[n];
        var widths = new int[n];
        int maxW = 1;

        try
        {
            for (int i = 0; i < n; i++)
            {
                var src = crops[i];
                if (src == null || src.Width <= 0 || src.Height <= 0)
                {
                    resized[i] = new SKBitmap(1, ModelHeight);
                    widths[i] = 1;
                    continue;
                }

                double scale = ModelHeight / (double)src.Height;
                int w = (int)Math.Ceiling(src.Width * scale);
                if (w < 1) w = 1;
                if (w > MaxWidth) w = MaxWidth;

                var dst = new SKBitmap(w, ModelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var canvas = new SKCanvas(dst))
                {
                    canvas.Clear(SKColors.Black);
#pragma warning disable CS0618
                    using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
#pragma warning restore CS0618
                    canvas.DrawBitmap(src, new SKRect(0, 0, w, ModelHeight), paint);
                }
                resized[i] = dst;
                widths[i] = w;
                if (w > maxW) maxW = w;
            }

            // ── Build tensor [N, 3, 48, maxW] ─────────────────────────
            var tensor = new DenseTensor<float>(new[] { n, 3, ModelHeight, maxW });

            for (int b = 0; b < n; b++)
            {
                var img = resized[b];
                int w = widths[b];
                var pixels = img.Pixels;   // SKColor[], RGBA layout

                for (int y = 0; y < ModelHeight; y++)
                {
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        var p = pixels[rowBase + x];
                        // PaddleOCR normalization: (v/255 - 0.5) / 0.5  →  [-1, 1]
                        tensor[b, 0, y, x] = (p.Red   / 255f - 0.5f) / 0.5f;
                        tensor[b, 1, y, x] = (p.Green / 255f - 0.5f) / 0.5f;
                        tensor[b, 2, y, x] = (p.Blue  / 255f - 0.5f) / 0.5f;
                    }
                }
            }

            // ── Inference ─────────────────────────────────────────────
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var outputs = _session.Run(inputs);
            var outTensor = outputs.First().AsTensor<float>();

            int dim0 = outTensor.Dimensions[0];   // batch
            int dim1 = outTensor.Dimensions[1];
            int dim2 = outTensor.Dimensions[2];

            // PP-OCRv5 rec emits [N, T, C]. Some variants emit [N, C, T].
            // Detect orientation by assuming the class dim is the smaller one
            // after batch: dict sizes are ~200, sequence lengths are ~20-80.
            bool tFirst = dim1 <= dim2;
            int seqLen     = tFirst ? dim1 : dim2;
            int numClasses = tFirst ? dim2 : dim1;

            int blank = 0;
            var results = new string[n];

            for (int b = 0; b < n; b++)
            {
                var sb = new StringBuilder(16);
                int prev = -1;

                for (int t = 0; t < seqLen; t++)
                {
                    int best = 0;
                    float bestVal = float.NegativeInfinity;

                    for (int c = 0; c < numClasses; c++)
                    {
                        float v = tFirst ? outTensor[b, t, c] : outTensor[b, c, t];
                        if (v > bestVal) { bestVal = v; best = c; }
                    }

                    if (best == blank) { prev = blank; continue; }
                    if (best == prev) { continue; }

                    int dictIdx = best - 1;
                    if (dictIdx >= 0 && dictIdx < _dict.Length)
                        sb.Append(_dict[dictIdx]);

                    prev = best;
                }

                results[b] = sb.ToString().Trim();
            }

            return results;
        }
        finally
        {
            foreach (var r in resized) r?.Dispose();
        }
    }

    public void Dispose() => _session?.Dispose();
}