using System.Drawing;

namespace IndicOcr.Core;

/// <summary>Languages the v1 engine can read. Hindi uses the Devanagari model (also Marathi).</summary>
public enum OcrLanguage { English, Telugu, Hindi, Tamil, Kannada }

/// <summary>One recognised text line. Box and Polygon are in page pixel coordinates.</summary>
public sealed record OcrLine(string Text, float Confidence, OcrLanguage Language, RectangleF Box, PointF[] Polygon);

/// <summary>Result for one page.</summary>
public sealed record OcrPage(int PageIndex, int Width, int Height, IReadOnlyList<OcrLine> Lines, TimeSpan Elapsed)
{
    /// <summary>Line texts joined with \n.</summary>
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

public sealed record OcrEngineOptions
{
    /// <summary>Longest side of the image fed to the detector (rounded to a multiple of 32).</summary>
    public int MaxSideLength { get; init; } = 1280;
    /// <summary>Lines per recognizer batch (lines are sorted by width first, so batches pad little).</summary>
    public int RecBatchSize { get; init; } = 3;
    /// <summary>Recognizer sessions run in parallel on different batches; each gets IntraOpThreads (or cores / workers when 0).</summary>
    public int RecWorkers { get; init; } = 1;
    /// <summary>Let ONNX Runtime worker threads spin while waiting (lower latency, burns CPU).</summary>
    public bool AllowSpinning { get; init; } = true;
    /// <summary>
    /// Language rule: when exactly one Indic language (Telugu, Hindi or Tamil, all PP-OCRv5) is selected together with English,
    /// only the Indic recognizer runs on each line, because its dictionary already contains A-Z, a-z and 0-9.
    /// Set false to always run every selected recognizer and keep the most confident result.
    /// </summary>
    public bool EnglishViaIndicRecognizer { get; init; } = true;
    /// <summary>ONNX Runtime intra-op threads per session; 0 = half the logical processors (at least 1, at most 8), because hyper-threads gave no speed-up on CPU inference.</summary>
    public int IntraOpThreads { get; init; } = 0;
    /// <summary>Minimum mean probability inside a detected box.</summary>
    public float BoxThreshold { get; init; } = 0.6f;
    /// <summary>Use the DirectML execution provider if the DirectML ONNX Runtime package is present; otherwise CPU.</summary>
    public bool UseDirectML { get; init; } = false;
}
