using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IndicOcr.Core;
using IndicOcr.App.Services;

namespace IndicOcr.App.Models;

public abstract class ObservableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One recognised line. Text is editable; the original OcrLine is kept for geometry.</summary>
public sealed class LineVm : ObservableBase
{
    private string _text;

    public LineVm(OcrLine source)
    {
        Source = source;
        _text = source.Text;
    }

    public OcrLine Source { get; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value ?? string.Empty);
    }

    public string ConfidenceText => ((int)Math.Round(Source.Confidence * 100)).ToString() + "%";

    public OcrLine ToEdited() => Source with { Text = _text };
}

/// <summary>
/// One page: the editable lines plus the path of the page image, stored as a JPEG in the session cache
/// folder (not kept in memory). Dispose deletes that file.
/// </summary>
public sealed class PageVm : IDisposable
{
    public PageVm(string imagePath, int width, int height, OcrPage page)
    {
        ImagePath = imagePath;
        Width = width;
        Height = height;
        Page = page;
        Lines = page.Lines.Select(l => new LineVm(l)).ToList();
    }

    public string ImagePath { get; }
    public int Width { get; }
    public int Height { get; }
    public OcrPage Page { get; }
    public List<LineVm> Lines { get; }

    public OcrPage ToEditedPage()
        => Page with { Lines = Lines.Select(l => l.ToEdited()).ToList() };

    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>"Detected: Telugu, English", or empty when the page was read with explicit languages (nothing detected).</summary>
    public string DetectedText
        => Page.DetectedLanguages.Count == 0
            ? string.Empty
            : "Detected: " + string.Join(", ", Page.DetectedLanguages.Select(LanguageNames.Display));

    public void Dispose()
    {
        PageCache.Delete(ImagePath);
    }
}

public static class LanguageNames
{
    public static string Display(OcrLanguage language) => language switch
    {
        OcrLanguage.Hindi => "Hindi / Marathi",
        OcrLanguage.Auto => "Auto-detect",
        _ => language.ToString(),
    };
}

public enum DocumentState { Queued, Processing, Done, Failed }

public sealed class DocumentVm : ObservableBase, IDisposable
{
    private DocumentState _state = DocumentState.Queued;
    private string _detail = string.Empty;
    private TimeSpan _elapsed = TimeSpan.Zero;

    public DocumentVm(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<PageVm> Pages { get; } = new();

    /// <summary>The languages this document was recognised with. The Excel export reads table cells again with the same languages, whatever the language boxes say by then.</summary>
    public IReadOnlyList<OcrLanguage> Languages { get; set; } = Array.Empty<OcrLanguage>();

    public DocumentState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) Raise(nameof(StatusText)); }
    }

    public string StatusText
    {
        get
        {
            switch (_state)
            {
                case DocumentState.Queued: return "Queued";
                case DocumentState.Processing:
                    return Pages.Count == 0 ? "Reading..." : "Reading... " + Pages.Count + " page(s) done";
                case DocumentState.Done:
                    {
                        int n = Pages.Count;
                        string per = n > 0 ? (_elapsed.TotalSeconds / n).ToString("0.0") + " s/page" : "no pages";
                        return n + " page(s), " + per;
                    }
                default: return "Failed: " + _detail;
            }
        }
    }

    public void MarkProcessing() { _detail = string.Empty; State = DocumentState.Processing; }

    public void AddPage(PageVm page)
    {
        Pages.Add(page);
        _elapsed += page.Page.Elapsed;
        Raise(nameof(StatusText));
    }

    public void MarkDone() => State = DocumentState.Done;

    public void MarkFailed(string reason)
    {
        _detail = reason;
        State = DocumentState.Failed;
        Raise(nameof(StatusText));
    }

    public string FullText()
        => string.Join(Environment.NewLine + Environment.NewLine, Pages.Select(p => p.Text));

    public void Dispose()
    {
        foreach (var p in Pages) p.Dispose();
        Pages.Clear();
    }
}
