using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IndicOcr.Core.Internal;

internal static class SessionFactory
{
    static SessionFactory()
    {
        // ONNX Runtime Windows builds emit TraceLogging telemetry by default; the product promises none.
        OrtEnv.Instance().DisableTelemetryEvents();
    }

    /// <summary>Half the logical processors, 1..8 (measured: on a 4-vCPU/2-core box 2 threads beat 1, 3 and 4).</summary>
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public static InferenceSession Create(string path, OcrEngineOptions o, int intraThreads = -1)
    {
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false, // dynamic input shapes
        };
        int threads = intraThreads > 0 ? intraThreads : o.IntraOpThreads > 0 ? o.IntraOpThreads : DefaultThreads;
        so.IntraOpNumThreads = threads;
        if (!o.AllowSpinning) so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        if (o.UseDirectML) TryAppendDirectML(so);
        return new InferenceSession(path, so);
    }

    /// <summary>
    /// Hook for the DirectML build: Microsoft.ML.OnnxRuntime.DirectML adds SessionOptions.AppendExecutionProvider_DML.
    /// With the CPU-only package the method does not exist and this silently stays on CPU.
    /// </summary>
    static void TryAppendDirectML(SessionOptions so)
    {
        var m = typeof(SessionOptions).GetMethod("AppendExecutionProvider_DML", new[] { typeof(int) });
        if (m == null) return;
        try { m.Invoke(so, new object[] { 0 }); } catch { /* fall back to CPU */ }
    }
}

internal sealed class Detector : IDisposable
{
    readonly InferenceSession _s;
    readonly string _input;
    readonly OcrEngineOptions _opt;

    public Detector(string path, OcrEngineOptions opt)
    {
        _opt = opt;
        _s = SessionFactory.Create(path, opt);
        _input = _s.InputMetadata.Keys.First();
    }

    /// <summary>Detects text boxes (ordered quads in page pixel coordinates).</summary>
    public List<(Pt[] Box, float Score)> Detect(PixelImage img)
    {
        var (w, h) = Preprocess.DetSize(img.Width, img.Height, _opt.MaxSideLength);
        var data = Preprocess.DetTensor(img, w, h);
        using var results = _s.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(data, new[] { 1, 3, h, w })) });
        var outT = results.First().AsTensor<float>();
        var dims = outT.Dimensions.ToArray();
        int oh = dims[^2], ow = dims[^1];
        var prob = outT.ToArray();
        return DbPostProcessor.Extract(prob, ow, oh, img.Width, img.Height,
            new DbParams { BoxThreshold = _opt.BoxThreshold });
    }

    public void Dispose() => _s.Dispose();
}

internal sealed class Recognizer : IDisposable
{
    readonly InferenceSession[] _s;
    readonly string _input;
    readonly CharTable _table;
    readonly int _batch;
    int _validated;

    public Recognizer(string onnxPath, string dictPath, OcrEngineOptions opt)
    {
        _table = CharTable.Load(dictPath);
        int workers = Math.Max(1, opt.RecWorkers);
        int threads = opt.IntraOpThreads > 0 ? opt.IntraOpThreads : Math.Max(1, SessionFactory.DefaultThreads / workers);
        _s = new InferenceSession[workers];
        for (int i = 0; i < workers; i++) _s[i] = SessionFactory.Create(onnxPath, opt, threads);
        _batch = Math.Max(1, opt.RecBatchSize);
        _input = _s[0].InputMetadata.Keys.First();
        // Static output class dimension (when declared) can be validated up front.
        var od = _s[0].OutputMetadata.Values.First().Dimensions;
        if (od.Length == 3 && od[2] > 0) { _table.Validate(od[2], Path.GetFileName(onnxPath)); _validated = 1; }
    }

    public List<(string Text, float Confidence)> Recognize(IReadOnlyList<Preprocess.LineCrop> crops)
    {
        var res = new (string, float)[crops.Count];
        var order = Enumerable.Range(0, crops.Count).OrderBy(i => crops[i].Width).ToArray();
        int nBatches = (order.Length + _batch - 1) / _batch;
        // Longest batches first so the parallel workers finish together.
        var batchIds = Enumerable.Range(0, nBatches).Reverse().ToArray();
        int workers = Math.Min(_s.Length, nBatches);
        if (workers <= 1)
        {
            foreach (var b in batchIds) RunBatch(_s[0], order, b, res, crops);
        }
        else
        {
            int next = -1;
            var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
            {
                int k;
                while ((k = Interlocked.Increment(ref next)) < batchIds.Length) RunBatch(_s[w], order, batchIds[k], res, crops);
            })).ToArray();
            Task.WaitAll(tasks);
        }
        return res.ToList();
    }

    void RunBatch(InferenceSession s, int[] order, int b, (string, float)[] res, IReadOnlyList<Preprocess.LineCrop> crops)
    {
        var idx = order.Skip(b * _batch).Take(_batch).ToArray();
        var batch = idx.Select(i => crops[i]).ToList();
        int width = Math.Max(Preprocess.RecMinWidth, batch.Max(c => c.Width));
        var data = Preprocess.RecBatchTensor(batch, width);
        using var results = s.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(data, new[] { batch.Count, 3, Preprocess.RecHeight, width })) });
        var t = results.First().AsTensor<float>();
        var d = t.Dimensions.ToArray();
        int n = d[0], steps = d[1], c = d[2];
        if (Interlocked.Exchange(ref _validated, 1) == 0) _table.Validate(c);
        var all = t.ToArray();
        for (int k = 0; k < n; k++)
            res[idx[k]] = CtcDecoder.Decode(all.AsSpan(k * steps * c, steps * c), steps, c, _table);
    }

    public void Dispose() { foreach (var s in _s) s.Dispose(); }
}
