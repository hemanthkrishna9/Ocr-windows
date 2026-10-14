using System.Collections.ObjectModel;
using IndicOcr.App.Models;
using IndicOcr.App.Services;
using IndicOcr.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using IOPath = System.IO.Path;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace IndicOcr.App;

public sealed partial class MainWindow : Window
{
    // Order of the tick boxes. Labels come from LanguageNames; "(beta)" comes from OcrEngine.IsBeta.
    private static readonly OcrLanguage[] PickerLanguages =
    {
        OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.Tamil, OcrLanguage.Kannada, OcrLanguage.English,
    };

    private static bool IsIndic(OcrLanguage l) => l != OcrLanguage.English && l != OcrLanguage.Auto;

    private readonly IntPtr _hwnd;
    private readonly ObservableCollection<DocumentVm> _docs = new();
    private readonly Dictionary<OcrLanguage, CheckBox> _langChecks = new();
    private readonly HashSet<OcrLanguage> _availableLangs = new();
    private CheckBox? _autoCheck;
    private TextBlock? _langHint;
    private readonly Dictionary<LineVm, Rectangle> _boxes = new();
    // The engine is thread-safe, so documents and snips use it at the same time. This only tracks
    // active users so the engine is never disposed while something is still running on it.
    private readonly UseTracker _engineUse = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HotkeyService _hotkey = new();
    private readonly AppSettings _settings = SettingsService.Load();

    private string? _modelsDir;
    private Task<OcrEngine>? _engineTask;
    private Task _queueTask = Task.CompletedTask;
    private bool _queueRunning;
    private int _exportRunning;   // exports in flight (they read the cached page images)
    private int _previewBusy;     // page previews being loaded
    private bool _closing;
    private bool _shutdownDone;
    private bool _snipActive;
    private bool _updatingLanguageUi;
    private DocumentVm? _currentDoc;
    private PageVm? _currentPage;
    private int _pageIndex;
    private int _showVersion;
    private string? _hotkeyError;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Indic OCR (Offline)";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        QueueList.ItemsSource = _docs;
        RedactToggle.IsChecked = _settings.RedactIds;
        UpdateRedactUi();
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        _ = Task.Run(PageCache.CleanStale); // leftovers of a crashed earlier session

        ResolveModels();

        _hotkey.Pressed += OnHotkeyPressed;
        _hotkeyError = _hotkey.Register(_hwnd, _settings.Hotkey);
        SnipButton.Content = "Snip text (" + _hotkey.Display + ")";
        if (_hotkeyError != null)
        {
            HotkeyInfo.Text = _hotkeyError;
            HotkeyInfo.Foreground = new SolidColorBrush(Colors.OrangeRed);
            ToolTipService.SetToolTip(HotkeyInfo, _hotkeyError);
            SetStatus(_hotkeyError);
        }
        else
        {
            HotkeyInfo.Text = "Snip hotkey: " + _hotkey.Display;
        }

        UpdateButtons();
    }

    // ---------------------------------------------------------------- models and languages

    private void ResolveModels()
    {
        var result = ModelLocator.Resolve(Environment.GetCommandLineArgs());
        _modelsDir = result.Directory;

        bool found = _modelsDir != null;
        FirstRunPanel.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        MainPanel.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        SearchedText.Text = "Places searched, in order:\n" + string.Join("\n", result.Searched.Select(s => "  " + s));

        BuildLanguageChecks();
        SetStatus(found ? "Ready. Models: " + _modelsDir : "No language models found.");
        UpdateButtons();
    }

    private void BuildLanguageChecks()
    {
        LanguagePanel.Children.Clear();
        _langChecks.Clear();
        _availableLangs.Clear();
        _autoCheck = null;
        _langHint = null;

        IReadOnlyList<OcrLanguage> available = Array.Empty<OcrLanguage>();
        if (_modelsDir != null)
        {
            try { available = OcrEngine.AvailableLanguages(_modelsDir); }
            catch (Exception ex) { AppLog.Error("AvailableLanguages", ex); }
        }
        foreach (var l in available) _availableLangs.Add(l);

        _updatingLanguageUi = true;

        // Auto-detect needs at least one Indic model to choose between.
        bool autoPossible = available.Any(IsIndic);
        _autoCheck = new CheckBox
        {
            Content = "Auto-detect language",
            IsEnabled = autoPossible,
            IsChecked = autoPossible && _settings.AutoDetect == true,
            MinWidth = 0,
        };
        ToolTipService.SetToolTip(_autoCheck, autoPossible
            ? "Finds the script of each line by itself, so mixed pages need no setup. English is always found."
            : "Needs at least one Telugu, Hindi, Tamil or Kannada model");
        _autoCheck.Checked += Auto_Changed;
        _autoCheck.Unchecked += Auto_Changed;
        LanguagePanel.Children.Add(_autoCheck);

        _langHint = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.8, Margin = new Thickness(12, 0, 4, 0) };
        LanguagePanel.Children.Add(_langHint);

        foreach (var lang in PickerLanguages)
        {
            bool enabled = available.Contains(lang);
            string label = LanguageNames.Display(lang) + (OcrEngine.IsBeta(lang) ? " (beta)" : "");
            var cb = new CheckBox
            {
                Content = label,
                IsEnabled = enabled,
                IsChecked = enabled && _settings.Languages.Contains(lang.ToString()),
                MinWidth = 0,
                Tag = lang,
            };
            cb.Checked += Language_Changed;
            cb.Unchecked += Language_Changed;
            _langChecks[lang] = cb;
            LanguagePanel.Children.Add(cb);
        }
        _updatingLanguageUi = false;
        ApplyAutoUi();
    }

    private bool AutoOn => _autoCheck is { IsEnabled: true, IsChecked: true };

    /// <summary>
    /// With Auto on, the Indic boxes stay usable as an optional restriction ("only these scripts"; none ticked means all installed
    /// ones). That is Core's meaning of Auto plus a list, and it helps when the user knows a page is never, say, Kannada. English has
    /// no meaning there (Latin text is always labelled English), so its box is greyed out.
    /// </summary>
    private void ApplyAutoUi()
    {
        bool auto = AutoOn;
        if (_langHint != null)
            _langHint.Text = auto ? "Only these scripts (optional):" : "Languages:";
        foreach (var (lang, cb) in _langChecks)
        {
            bool avail = _availableLangs.Contains(lang);
            cb.IsEnabled = avail && !(auto && lang == OcrLanguage.English);
            if (!avail) ToolTipService.SetToolTip(cb, "No model installed for this language");
            else if (auto && lang == OcrLanguage.English) ToolTipService.SetToolTip(cb, "Auto-detect always finds English as well");
            else if (OcrEngine.IsBeta(lang)) ToolTipService.SetToolTip(cb, "Uses an older model; expect many errors");
            else ToolTipService.SetToolTip(cb, auto ? "Tick to limit auto-detect to the ticked scripts" : null);
        }
    }

    /// <summary>What the engine is given: Auto plus the ticked Indic scripts, or just the ticked languages.</summary>
    private List<OcrLanguage> SelectedLanguages()
    {
        var ticked = _langChecks.Where(kv => _availableLangs.Contains(kv.Key) && kv.Value.IsChecked == true)
                                .Select(kv => kv.Key).ToList();
        if (!AutoOn) return ticked;
        var result = new List<OcrLanguage> { OcrLanguage.Auto };
        result.AddRange(ticked.Where(IsIndic));
        return result;
    }

    private void SaveLanguageSettings()
    {
        // English stays remembered even while its box is greyed out by Auto.
        _settings.Languages = _langChecks.Where(kv => _availableLangs.Contains(kv.Key) && kv.Value.IsChecked == true)
                                         .Select(kv => kv.Key.ToString()).ToList();
        _settings.AutoDetect = AutoOn;
        SettingsService.Save(_settings);
    }

    private void Auto_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLanguageUi) return;
        if (!AutoOn && !_langChecks.Any(kv => kv.Value.IsChecked == true))
        {
            // Switching Auto off with nothing ticked would leave nothing to read: fall back to a sensible pair.
            _updatingLanguageUi = true;
            OcrLanguage? first = PickerLanguages.Where(l => IsIndic(l) && _availableLangs.Contains(l))
                                                .Select(l => (OcrLanguage?)l).FirstOrDefault();
            if (first != null) _langChecks[first.Value].IsChecked = true;
            if (_availableLangs.Contains(OcrLanguage.English)) _langChecks[OcrLanguage.English].IsChecked = true;
            _updatingLanguageUi = false;
        }
        ApplyAutoUi();
        SaveLanguageSettings();
        _ = StartQueueAsync();
    }

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLanguageUi) return;
        SaveLanguageSettings();
        _ = StartQueueAsync(); // queued documents may have been waiting for a language
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_queueRunning || _engineUse.Count > 0)
        {
            SetStatus("Wait for the current work to finish, then check again.");
            return;
        }
        string? before = _modelsDir;
        ResolveModels();
        if (!string.Equals(before, _modelsDir, StringComparison.OrdinalIgnoreCase))
            ResetEngine(); // a different models folder needs a fresh engine
        _ = StartQueueAsync(); // documents dropped before the models appeared are still queued
    }

    /// <summary>Disposes the cached engine. Only call when nothing is using it.</summary>
    private void ResetEngine()
    {
        var t = _engineTask;
        _engineTask = null;
        if (t is { IsCompletedSuccessfully: true })
        {
            var engine = t.Result;
            _ = Task.Run(() => { try { engine.Dispose(); } catch { /* ignore */ } });
        }
    }

    // ---------------------------------------------------------------- input: pickers and drop

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            foreach (string ext in InputFiles.SupportedExtensions) picker.FileTypeFilter.Add(ext);
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            await AddPathsAsync(files.Select(f => f.Path).ToList());
        }
        catch (Exception ex)
        {
            AppLog.Error("Open files", ex);
            SetStatus("Could not open the file picker.");
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            await AddPathsAsync(new[] { folder.Path });
        }
        catch (Exception ex)
        {
            AppLog.Error("Open folder", ex);
            SetStatus("Could not open the folder picker.");
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            // Accepted even without models so the drop can explain what is missing.
            e.DragUIOverride.Caption = _modelsDir == null ? "Language models are missing" : "Read with Indic OCR";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        DragOperationDeferral? deferral = e.GetDeferral();
        try
        {
            if (_modelsDir == null)
            {
                SetStatus("No language models found. Add them (see the first page) and press Check again.");
                return;
            }
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            deferral.Complete();
            deferral = null;
            await AddPathsAsync(paths);
        }
        catch (Exception ex)
        {
            AppLog.Error("Drop", ex);
            SetStatus("Could not read the dropped items.");
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (_modelsDir == null)
        {
            SetStatus("No language models found. Add them (see the first page) and press Check again.");
            return;
        }

        SetStatus("Looking for files...");
        List<(string Path, string? Error)> found;
        try
        {
            // Walking a large folder tree can take a while: keep it off the UI thread.
            found = await Task.Run(() =>
            {
                var list = new List<(string Path, string? Error)>();
                foreach (string p in paths)
                {
                    try
                    {
                        if (Directory.Exists(p))
                        {
                            foreach (string f in InputFiles.Enumerate(p, recursive: true))
                                list.Add((f, null));
                        }
                        else if (File.Exists(p))
                        {
                            list.Add((p, InputFiles.IsSupported(p) ? null : "Unsupported file type"));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("AddPaths", ex);
                        list.Add((p, "Could not read this item"));
                    }
                }
                return list;
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("AddPaths", ex);
            SetStatus("Could not read the selected items.");
            return;
        }

        foreach (var (path, error) in found)
        {
            var doc = new DocumentVm(path);
            if (error != null) doc.MarkFailed(error);
            _docs.Add(doc);
        }

        if (found.Count == 0) SetStatus("No supported images or PDFs found.");
        else SetStatus("Added " + found.Count + " item(s).");
        if (QueueList.SelectedItem == null && _docs.Count > 0) QueueList.SelectedIndex = 0;
        UpdateButtons();
        _ = StartQueueAsync();
    }

    private bool CanClear => !_queueRunning && _exportRunning == 0 && _previewBusy == 0 && !_snipActive;

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!CanClear)
        {
            SetStatus("Wait for reading, saving or the page preview to finish before clearing the queue.");
            return;
        }
        ShowDocument(null);
        QueueList.SelectedItem = null;
        foreach (var d in _docs) d.Dispose();
        _docs.Clear();
        PageCache.DeleteSession();
        UpdateButtons();
    }

    // ---------------------------------------------------------------- processing

    private Task OnUi(Action action)
    {
        var tcs = new TaskCompletionSource();
        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!queued) tcs.SetException(new InvalidOperationException("The window is closing."));
        return tcs.Task;
    }

    /// <summary>Single-flight: concurrent callers share one engine creation. UI thread only.</summary>
    private Task<OcrEngine> GetEngineAsync()
    {
        var t = _engineTask;
        if (t != null && !t.IsFaulted && !t.IsCanceled) return t;
        string? dir = _modelsDir;
        if (dir == null) return Task.FromException<OcrEngine>(new InvalidOperationException("No models directory."));
        t = Task.Run(() => new OcrEngine(dir));
        _engineTask = t;
        return t;
    }

    private Task StartQueueAsync()
    {
        if (_queueRunning || _modelsDir == null || _closing) return _queueTask;
        _queueRunning = true; // set synchronously, before the first await
        _queueTask = RunQueueAsync();
        return _queueTask;
    }

    private async Task RunQueueAsync()
    {
        BusyBar.Visibility = Visibility.Visible;
        UpdateButtons();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var doc = _docs.FirstOrDefault(d => d.State == DocumentState.Queued);
                if (doc == null) break;

                var langs = SelectedLanguages();
                if (langs.Count == 0)
                {
                    SetStatus("Select at least one language to start reading.");
                    break;
                }

                SetStatus("Reading " + doc.Name + "...");
                await ProcessDocumentAsync(doc, langs);
            }
        }
        finally
        {
            _queueRunning = false;
            BusyBar.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
        if (!_closing && _docs.All(d => d.State != DocumentState.Queued) && _docs.Count > 0 && SelectedLanguages().Count > 0)
            SetStatus("Done.");
    }

    private async Task ProcessDocumentAsync(DocumentVm doc, List<OcrLanguage> langs)
    {
        doc.MarkProcessing();
        if (!_engineUse.TryEnter())
        {
            doc.MarkFailed("Cancelled"); // the app is closing
            return;
        }
        try
        {
            OcrEngine engine = await GetEngineAsync();
            var ct = _cts.Token;
            await Task.Run(async () =>
            {
                await foreach (var (image, page) in engine.RecognizeFileAsync(doc.Path, langs, ct))
                {
                    // Keep only a JPEG on disk; the full-resolution bitmap is released straight away.
                    PageVm vm;
                    try
                    {
                        string cached = PageCache.Save(image);
                        vm = new PageVm(cached, image.Width, image.Height, page);
                    }
                    finally
                    {
                        image.Dispose();
                    }
                    try
                    {
                        await OnUi(() => AddPage(doc, vm));
                    }
                    catch
                    {
                        vm.Dispose();
                        throw;
                    }
                }
            }, ct);
            doc.MarkDone();
        }
        catch (OperationCanceledException)
        {
            doc.MarkFailed("Cancelled");
        }
        catch (OutOfMemoryException)
        {
            doc.MarkFailed("The image is too large to process");
        }
        catch (Exception ex)
        {
            AppLog.Error("Process document", ex);
            doc.MarkFailed(DescribeError(ex));
        }
        finally
        {
            _engineUse.Exit();
        }
        UpdateButtons();
    }

    private static string DescribeError(Exception ex)
    {
        if (ex is FileNotFoundException) return "File not found";
        if (ex is UnauthorizedAccessException) return "Access denied";
        if (ex is IOException) return "Could not read the file";
        string msg = ex.Message;
        if (string.IsNullOrWhiteSpace(msg)) return "The file could not be read (corrupt or unsupported)";
        return msg.Length > 200 ? msg.Substring(0, 200) : msg;
    }

    private void AddPage(DocumentVm doc, PageVm page)
    {
        doc.AddPage(page);
        if (ReferenceEquals(doc, _currentDoc) && doc.Pages.Count == 1) ShowPage(0);
        else if (ReferenceEquals(doc, _currentDoc)) UpdatePageNav();
        UpdateButtons();
    }

    // ---------------------------------------------------------------- viewer

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowDocument(QueueList.SelectedItem as DocumentVm);

    private void ShowDocument(DocumentVm? doc)
    {
        _currentDoc = doc;
        if (doc != null && doc.Pages.Count > 0) ShowPage(0);
        else ShowPage(-1);
        UpdateButtons();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => ShowPage(_pageIndex + 1);

    private void ShowPage(int index) => _ = ShowPageAsync(index);

    private async Task ShowPageAsync(int index)
    {
        int version = ++_showVersion;
        _previewBusy++;
        try
        {
            BoxCanvas.Children.Clear();
            _boxes.Clear();
            LinesList.ItemsSource = null;
            PageImage.Source = null;
            RedactCanvas.Children.Clear();
            DetectedLabel.Text = string.Empty;

            var doc = _currentDoc;
            if (doc == null || index < 0 || index >= doc.Pages.Count)
            {
                _currentPage = null;
                _pageIndex = 0;
                UpdatePageNav();
                return;
            }

            var page = doc.Pages[index];
            _currentPage = page;
            _pageIndex = index;
            UpdatePageNav();
            DetectedLabel.Text = page.DetectedText;

            // The page is already a JPEG in the cache folder: read it as is, no re-encode. Decode at
            // most 2000 px wide; the Image element scales it to the page size.
            byte[] jpeg = await Task.Run(() => File.ReadAllBytes(page.ImagePath));
            var bitmap = await ImageHelper.ToBitmapImageAsync(jpeg, Math.Min(page.Width, 2000));
            if (version != _showVersion) return; // a newer ShowPage call took over

            int w = page.Width;
            int h = page.Height;
            PageGrid.Width = w;
            PageGrid.Height = h;
            BoxCanvas.Width = w;
            BoxCanvas.Height = h;
            RedactCanvas.Width = w;
            RedactCanvas.Height = h;
            PageImage.Width = w;
            PageImage.Height = h;
            PageImage.Source = bitmap;

            double stroke = Math.Max(2, w / 400.0);
            foreach (var line in page.Lines)
            {
                var b = line.Source.Box;
                var rect = new Rectangle
                {
                    Width = Math.Max(1, b.Width),
                    Height = Math.Max(1, b.Height),
                    StrokeThickness = stroke,
                    Fill = new SolidColorBrush(Colors.Transparent), // transparent but hit-testable
                    Tag = line,
                };
                Canvas.SetLeft(rect, b.X);
                Canvas.SetTop(rect, b.Y);
                rect.Tapped += Box_Tapped;
                _boxes[line] = rect;
                BoxCanvas.Children.Add(rect);
            }
            LinesList.ItemsSource = page.Lines;
            RefreshBoxStyles();
        }
        catch (Exception ex)
        {
            AppLog.Error("ShowPage", ex);
            SetStatus("Could not display this page.");
        }
        finally
        {
            _previewBusy--;
            UpdateButtons();
        }
    }

    private void UpdatePageNav()
    {
        int n = _currentDoc?.Pages.Count ?? 0;
        PageLabel.Text = n == 0 ? "No page" : "Page " + (_pageIndex + 1) + " of " + n;
        PrevPageButton.IsEnabled = _pageIndex > 0;
        NextPageButton.IsEnabled = _pageIndex + 1 < n;
    }

    private void Box_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Rectangle r && r.Tag is LineVm line)
        {
            LinesList.SelectedItem = line;
            LinesList.ScrollIntoView(line);
        }
    }

    private void LinesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshBoxStyles();

    private void LineBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is LineVm line)
            LinesList.SelectedItem = line;
    }

    private void RefreshBoxStyles()
    {
        var selected = LinesList.SelectedItem as LineVm;
        var normal = new SolidColorBrush(Colors.DodgerBlue);
        var highlight = new SolidColorBrush(Colors.OrangeRed);
        foreach (var kv in _boxes)
        {
            bool isSel = ReferenceEquals(kv.Key, selected);
            kv.Value.Stroke = isSel ? highlight : normal;
            kv.Value.Fill = new SolidColorBrush(isSel ? Windows.UI.Color.FromArgb(0x30, 0xFF, 0x45, 0x00) : Colors.Transparent);
        }
    }

    // ---------------------------------------------------------------- export

    private DocumentVm? ExportableDocument()
    {
        var doc = _currentDoc;
        if (doc == null || doc.Pages.Count == 0)
        {
            SetStatus("Select a document with recognised pages first.");
            return null;
        }
        return doc;
    }

    private bool RedactionOn => RedactToggle.IsChecked;

    private void RedactToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RedactIds = RedactToggle.IsChecked;
        SettingsService.Save(_settings);
        UpdateRedactUi();
        SetStatus(RedactionOn
            ? "Redaction is on: Copy all text and every export ask you to review the ID numbers first."
            : "Redaction is off.");
    }

    private void UpdateRedactUi()
        => CopyButton.Content = RedactionOn ? "Copy all text (redacted)" : "Copy all text";

    private async void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var doc = ExportableDocument();
            if (doc == null) return;
            string text;
            if (RedactionOn)
            {
                var pages = doc.Pages.Select(p => p.ToEditedPage()).ToList();
                var plan = await ReviewRedactionAsync(doc, pages);
                if (plan == null) { SetStatus("Copy cancelled."); return; }
                text = string.Join(Environment.NewLine + Environment.NewLine,
                    RedactPagesText(pages, plan).Select(p => string.Join(Environment.NewLine, p.Lines.Select(l => l.Text))));
            }
            else
            {
                text = doc.FullText();
            }
            if (CopyToClipboard(text)) SetStatus("Copied " + text.Length + " characters" + (RedactionOn ? " (redacted)" : ""));
            else SetStatus("Could not access the clipboard.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Copy all", ex);
            SetStatus("Could not copy the text.");
        }
    }

    private void SaveTxt_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("txt");
    private void SaveDocx_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("docx");
    private void SavePdf_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("pdf");
    private void SaveXlsx_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("xlsx");

    private async Task ExportAsync(string kind)
    {
        var doc = ExportableDocument();
        if (doc == null) return;
        _exportRunning++; // blocks Clear queue until the export (dialog, file picker and write) is finished
        UpdateButtons();
        bool engineEntered = false;
        string? outPath = null;
        bool redactRun = false;
        try
        {
            // Snapshot the edited lines on the UI thread; everything after this works on the snapshot.
            var pages = doc.Pages.Select(p => (Path: p.ImagePath, Page: p.ToEditedPage())).ToList();

            // Redaction: review first. Cancelling stops the export; nothing is written.
            List<List<PiiFinding>>? plan = null;
            if (RedactionOn)
            {
                plan = await ReviewRedactionAsync(doc, pages.Select(p => p.Page).ToList());
                if (plan == null) { SetStatus("Export cancelled."); return; }
            }
            bool redact = plan != null;
            redactRun = redact;

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = IOPath.GetFileNameWithoutExtension(doc.Name) + (redact ? ".redacted" : "");
            string label = kind switch
            {
                "txt" => "Text file",
                "docx" => "Word document",
                "xlsx" => "Excel workbook",
                _ => "Searchable PDF",
            };
            picker.FileTypeChoices.Add(label, new List<string> { "." + kind });
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;
            string path = file.Path;
            outPath = path;

            OcrEngine? engine = null;
            List<OcrLanguage>? langs = null;
            if (kind == "xlsx")
            {
                // Table cells are read again by the engine: same rule as every other engine user.
                if (!_engineUse.TryEnter()) { SetStatus("The app is closing."); return; }
                engineEntered = true;
                engine = await GetEngineAsync();
                langs = SelectedLanguages();
                if (langs.Count == 0) { SetStatus("Select at least one language first."); return; }
            }

            SetStatus("Saving...");
            BusyBar.Visibility = Visibility.Visible;
            var ct = _cts.Token;
            await Task.Run(() =>
            {
                switch (kind)
                {
                    case "txt":
                        TextExporter.Write(redact ? RedactPagesText(pages.Select(p => p.Page).ToList(), plan!) : pages.Select(p => p.Page), path);
                        break;
                    case "docx":
                        DocxExporter.Write(redact ? RedactPagesText(pages.Select(p => p.Page).ToList(), plan!) : pages.Select(p => p.Page), path);
                        break;
                    case "xlsx":
                        WriteXlsx(engine!, pages, plan, langs!, path, ct);
                        break;
                    default:
                        // null: Core looks in <exe dir>\fonts first, where the bundled Noto fonts are copied.
                        // Redacted: only the blacked-out bitmaps go to the exporter, never the stored JPEG.
                        if (redact) SearchablePdfExporter.Write(RedactedBitmaps(pages, plan!), path, null);
                        else SearchablePdfExporter.Write(ReadCachedJpegs(pages), path, null);
                        break;
                }
            });
            SetStatus("Saved " + file.Name + (redact ? " (redacted)" : ""));
        }
        catch (Exception ex)
        {
            AppLog.Error("Export " + kind, ex);
            // A failed redacted export must not leave a partly written file behind.
            if (redactRun && outPath != null) { try { File.Delete(outPath); } catch { /* best effort */ } }
            SetStatus("Could not save the file: " + DescribeError(ex));
        }
        finally
        {
            if (engineEntered) _engineUse.Exit();
            _exportRunning--;
            BusyBar.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Reads the cached page JPEGs lazily, one at a time, and hands the bytes straight to the exporter (no decode and no re-encode, so
    /// no quality loss). The exporter consumes the sequence page by page, so only one page's bytes are held at any moment.
    /// </summary>
    private static IEnumerable<(byte[] Jpeg, OcrPage Page)> ReadCachedJpegs(List<(string Path, OcrPage Page)> pages)
    {
        foreach (var (path, page) in pages)
            yield return (File.ReadAllBytes(path), page);
    }

    // ---------------------------------------------------------------- redaction and tables

    /// <summary>
    /// Decodes the cached page and, when there are findings, returns a blacked-out copy with the matching masked page. The caller owns
    /// (and disposes) the returned bitmap. The unredacted bitmap is disposed here.
    /// </summary>
    private static (SKBitmap Image, OcrPage Page) LoadMaybeRedacted(string path, OcrPage page, IReadOnlyList<PiiFinding>? findings, int pageNumber)
    {
        var src = SKBitmap.Decode(path) ?? throw new IOException("Could not read the stored page image.");
        if (findings == null || findings.Count == 0) return (src, page);
        try
        {
            return Redactor.Apply(src, page, findings);
        }
        catch (ArgumentException)
        {
            // Size mismatch or a finding that does not fit the page: never fall back to the unredacted image. Abort the export.
            throw new InvalidOperationException("Page " + pageNumber + " could not be redacted safely (its image does not match its text). Nothing was saved.");
        }
        finally
        {
            src.Dispose();
        }
    }

    /// <summary>One redacted page at a time, each bitmap disposed as soon as the exporter asks for the next one.</summary>
    private static IEnumerable<(SKBitmap Image, OcrPage Page)> RedactedBitmaps(List<(string Path, OcrPage Page)> pages, List<List<PiiFinding>> plan)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            var (image, page) = LoadMaybeRedacted(pages[i].Path, pages[i].Page, plan[i], i + 1);
            try
            {
                yield return (image, page);
            }
            finally
            {
                image.Dispose();
            }
        }
    }

    /// <summary>Text only: masks the selected findings in the page text (no image involved).</summary>
    private static List<OcrPage> RedactPagesText(List<OcrPage> pages, List<List<PiiFinding>> plan)
    {
        var result = new List<OcrPage>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
            result.Add(plan[i].Count == 0 ? pages[i] : Redactor.MaskText(pages[i], plan[i], 'X'));
        return result;
    }

    /// <summary>Runs on a worker thread. The caller holds an engine use for the whole call.</summary>
    private static void WriteXlsx(OcrEngine engine, List<(string Path, OcrPage Page)> pages, List<List<PiiFinding>>? plan,
        IReadOnlyList<OcrLanguage> langs, string path, CancellationToken ct)
    {
        var results = new List<(OcrPage Page, IReadOnlyList<OcrTable> Tables)>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // With redaction the tables come from the blacked-out bitmap and the masked page, so a hidden number cannot be read back.
            var (image, page) = LoadMaybeRedacted(pages[i].Path, pages[i].Page, plan?[i], i + 1);
            try
            {
                results.Add((page, TableExtractor.Extract(engine, image, page, langs)));
            }
            finally
            {
                image.Dispose();
            }
        }
        XlsxExporter.Write(results, path);
    }

    private static string KindLabel(PiiKind kind) => kind switch
    {
        PiiKind.Aadhaar => "Aadhaar",
        PiiKind.Vid => "Aadhaar VID",
        PiiKind.Pan => "PAN",
        PiiKind.Phone => "Phone",
        PiiKind.Email => "Email",
        _ => kind.ToString(),
    };

    private const string RedactionWarning = "Numbers written in Indian-language digits may not be detected. Check the pages before sharing.";

    private bool _dialogOpen;
    private ContentDialog? _activeDialog;

    /// <summary>
    /// Finds ID numbers on the (edited) pages and lets the user tick what to black out. Returns, per page, the findings to apply, or null
    /// when the user cancels. Findings are shown only in this dialog (masked) and are never logged.
    /// </summary>
    private async Task<List<List<PiiFinding>>?> ReviewRedactionAsync(DocumentVm doc, List<OcrPage> pages)
    {
        if (_dialogOpen) { SetStatus("Finish the open dialog first."); return null; }
        SetStatus("Looking for ID numbers...");
        BusyBar.Visibility = Visibility.Visible;
        List<IReadOnlyList<PiiFinding>> found;
        try
        {
            found = await Task.Run(() => pages.Select(p => Redactor.Find(p)).ToList());
        }
        finally
        {
            BusyBar.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
        }

        // One row per (page, GroupId): a number split over several OCR lines is one row; its checkbox covers all its findings.
        var items = new List<(int Page, List<PiiFinding> Findings, CheckBox Box)>();
        var list = new StackPanel { Spacing = 2 };
        for (int pi = 0; pi < found.Count; pi++)
        {
            foreach (var g in found[pi].GroupBy(f => f.GroupId))
            {
                var members = g.ToList();
                var f = members[0];
                var cb = new CheckBox
                {
                    IsChecked = true,
                    MinWidth = 0,
                    Content = "Page " + (pi + 1) + "   " + KindLabel(f.Kind) + "   " + f.MaskedText + (members.All(m => m.Verified) ? "" : "   (unverified)"),
                };
                items.Add((pi, members, cb));
                list.Children.Add(cb);
            }
        }

        void RefreshPreview()
        {
            RedactCanvas.Children.Clear();
            if (!ReferenceEquals(doc, _currentDoc)) return;
            double stroke = Math.Max(2, (_currentPage?.Width ?? 800) / 400.0);
            foreach (var (pageIndex, findings, box) in items)
            {
                if (pageIndex != _pageIndex || box.IsChecked != true) continue;
                foreach (var finding in findings)
                {
                    var pts = new PointCollection();
                    foreach (var q in finding.Quad) pts.Add(new Windows.Foundation.Point(q.X, q.Y));
                    RedactCanvas.Children.Add(new Polygon
                    {
                        Points = pts,
                        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xA0, 0, 0, 0)),
                        Stroke = new SolidColorBrush(Colors.Red),
                        StrokeThickness = stroke,
                    });
                }
            }
        }
        foreach (var it in items)
        {
            it.Box.Checked += (_, _) => RefreshPreview();
            it.Box.Unchecked += (_, _) => RefreshPreview();
        }

        var content = new StackPanel { Spacing = 8, MinWidth = 360 };
        ContentDialog dialog;
        if (items.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "No Aadhaar, PAN or phone numbers were found on these pages. " + RedactionWarning,
            });
            dialog = new ContentDialog
            {
                Title = "No ID numbers found",
                Content = content,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = items.Count + " possible ID number(s) found. Ticked ones are blacked out in the file and replaced by X in the text. Unticked ones stay readable. The page view shows the ticked areas. Unverified means the number could not be confirmed by its check digit; it is still ticked.",
            });
            content.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = RedactionWarning });
            dialog = new ContentDialog
            {
                Title = "Review ID numbers to redact",
                Content = content,
                PrimaryButtonText = "Redact and continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
        }
        dialog.XamlRoot = RootGrid.XamlRoot; // required, or ShowAsync throws
        dialog.RequestedTheme = RootGrid.ActualTheme;

        ContentDialogResult result;
        _dialogOpen = true;
        _activeDialog = dialog;
        RefreshPreview();
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            _activeDialog = null;
            RedactCanvas.Children.Clear();
        }
        if (result != ContentDialogResult.Primary) return null;

        var plan = new List<List<PiiFinding>>(pages.Count);
        for (int pi = 0; pi < pages.Count; pi++)
            plan.Add(items.Where(it => it.Page == pi && it.Box.IsChecked == true).SelectMany(it => it.Findings).ToList());
        return plan;
    }

    private bool CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            // Keep OCR'd text out of the cloud clipboard and the Win+V history.
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
            {
                IsRoamable = false,
                IsAllowedInHistory = false,
            });
            try { Clipboard.Flush(); } catch { /* flush is best effort */ }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Clipboard", ex);
            return false;
        }
    }

    // ---------------------------------------------------------------- screen snip

    private void OnHotkeyPressed() => _ = SnipAsync(hideMainWindow: false);

    private void Snip_Click(object sender, RoutedEventArgs e) => _ = SnipAsync(hideMainWindow: true);

    private async Task SnipAsync(bool hideMainWindow)
    {
        if (_snipActive || _closing) return;
        if (_modelsDir == null) { SetStatus("Add the language models first."); return; }
        var langs = SelectedLanguages();
        if (langs.Count == 0) { SetStatus("Select at least one language first."); return; }

        _snipActive = true;
        SKBitmap? capture = null;
        SKBitmap? crop = null;
        SKBitmap? upscaled = null;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        bool minimized = false;
        try
        {
            if (hideMainWindow && presenter != null)
            {
                // Started from the button: get this window out of the screenshot.
                presenter.Minimize();
                minimized = true;
                await Task.Delay(350); // let DWM repaint what was behind the window
            }

            var captured = await Task.Run(() =>
            {
                var bmp = ScreenCapture.CaptureVirtualScreen(out var b);
                return (Bitmap: bmp, Bounds: b);
            });
            capture = captured.Bitmap;
            byte[] jpeg = await Task.Run(() => ImageHelper.EncodeJpeg(captured.Bitmap));

            var overlay = new SnipOverlayWindow(jpeg, captured.Bounds);
            var selection = await overlay.SelectAsync();
            if (selection == null) return;

            crop = ScreenCapture.Crop(capture, selection.Value);
            SetStatus("Reading snip...");
            BusyBar.Visibility = Visibility.Visible;
            // Small on-screen text reads better when enlarged first.
            upscaled = ScreenCapture.UpscaleIfSmall(crop);
            var cropForOcr = upscaled ?? crop;

            if (!_engineUse.TryEnter()) return; // the app is closing
            string text;
            bool snipRedacted = false;
            try
            {
                var engine = await GetEngineAsync();
                bool redact = RedactionOn; // a snip has no review step (it must stay one keystroke): every ID number found is masked
                text = await Task.Run(() =>
                {
                    var snipPage = engine.Recognize(cropForOcr, langs);
                    if (redact) snipPage = RedactPagesText(new List<OcrPage> { snipPage }, new List<List<PiiFinding>> { Redactor.Find(snipPage).ToList() })[0];
                    return snipPage.Text;
                });
                snipRedacted = redact;
            }
            finally
            {
                _engineUse.Exit();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("No text found in the selection.");
                ShowToast("No text found");
            }
            else if (CopyToClipboard(text))
            {
                string msg = "Copied " + text.Length + " characters" + (snipRedacted ? " (redacted)" : "");
                SetStatus(msg);
                ShowToast(msg);
            }
            else
            {
                SetStatus("Could not access the clipboard.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Snip", ex);
            SetStatus("Snip failed: " + DescribeError(ex));
        }
        finally
        {
            upscaled?.Dispose();
            crop?.Dispose();
            capture?.Dispose();
            _snipActive = false;
            if (minimized && !_closing)
            {
                try { presenter?.Restore(); } catch { /* window already gone */ }
            }
            BusyBar.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private void ShowToast(string message)
    {
        try { new ToastWindow(message).ShowBriefly(); }
        catch (Exception ex) { AppLog.Error("Toast", ex); }
    }

    // ---------------------------------------------------------------- misc

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateButtons()
    {
        bool hasPages = _currentDoc != null && _currentDoc.Pages.Count > 0;
        CopyButton.IsEnabled = hasPages;
        ExportButton.IsEnabled = hasPages;
        SnipButton.IsEnabled = _modelsDir != null;
        OpenFilesButton.IsEnabled = _modelsDir != null;
        OpenFolderButton.IsEnabled = _modelsDir != null;
        ClearButton.IsEnabled = _docs.Count > 0 && CanClear;
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>
    /// Closing is cancelled first, the window is hidden, in-flight work is given a few seconds to stop,
    /// and only then are the engine and the cached images released and the window really closed.
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownDone) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { sender.Hide(); } catch { /* ignore */ }
        try
        {
            await ShutdownAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Shutdown", ex);
        }
        _shutdownDone = true;
        Close();
    }

    private async Task ShutdownAsync()
    {
        _cts.Cancel();
        try { _activeDialog?.Hide(); } catch { /* dialog already closing */ }
        _hotkey.Dispose();

        // No new engine users from here on; wait for the running ones (queue, snip) to leave.
        Task engineIdle = _engineUse.CloseAndWaitIdleAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
               !(engineIdle.IsCompleted && _queueTask.IsCompleted && _exportRunning == 0 && _previewBusy == 0 && !_snipActive))
        {
            await Task.Delay(50);
        }

        foreach (var d in _docs) d.Dispose();
        _docs.Clear();

        // Dispose the engine only if every user has really left it.
        var t = _engineTask;
        _engineTask = null;
        if (engineIdle.IsCompleted && t is { IsCompletedSuccessfully: true })
        {
            var engine = t.Result;
            await Task.Run(() => { try { engine.Dispose(); } catch { /* ignore */ } });
        }
        PageCache.DeleteSession();
    }

    /// <summary>Fallback if the window is closed without going through AppWindow.Closing.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_shutdownDone) return;
        _closing = true;
        _cts.Cancel();
        _hotkey.Dispose();
        foreach (var d in _docs) d.Dispose();
        _docs.Clear();

        var t = _engineTask;
        _engineTask = null;
        Task idle = _engineUse.CloseAndWaitIdleAsync();
        if (t is { IsCompletedSuccessfully: true })
        {
            var engine = t.Result;
            _ = Task.Run(async () =>
            {
                try
                {
                    await idle.WaitAsync(TimeSpan.FromSeconds(5));
                    engine.Dispose(); // only reached when every user has left
                }
                catch { /* timed out or process exiting: leave the engine alone */ }
            });
        }
        PageCache.DeleteSession();
    }
}
