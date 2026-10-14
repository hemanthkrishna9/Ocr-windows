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
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace IndicOcr.App;

public sealed partial class MainWindow : Window
{
    private static readonly (OcrLanguage Language, string Label)[] LanguageLabels =
    {
        (OcrLanguage.Telugu, "Telugu"),
        (OcrLanguage.Hindi, "Hindi / Marathi"),
        (OcrLanguage.Tamil, "Tamil"),
        (OcrLanguage.Kannada, "Kannada"),
        (OcrLanguage.English, "English"),
    };

    private readonly IntPtr _hwnd;
    private readonly ObservableCollection<DocumentVm> _docs = new();
    private readonly Dictionary<OcrLanguage, CheckBox> _langChecks = new();
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

        IReadOnlyList<OcrLanguage> available = Array.Empty<OcrLanguage>();
        if (_modelsDir != null)
        {
            try { available = OcrEngine.AvailableLanguages(_modelsDir); }
            catch (Exception ex) { AppLog.Error("AvailableLanguages", ex); }
        }

        _updatingLanguageUi = true;
        foreach (var (lang, label) in LanguageLabels)
        {
            bool enabled = available.Contains(lang);
            var cb = new CheckBox
            {
                Content = label,
                IsEnabled = enabled,
                IsChecked = enabled && _settings.Languages.Contains(lang.ToString()),
                MinWidth = 0,
                Tag = lang,
            };
            if (!enabled) ToolTipService.SetToolTip(cb, "No model installed for this language");
            cb.Checked += Language_Changed;
            cb.Unchecked += Language_Changed;
            _langChecks[lang] = cb;
            LanguagePanel.Children.Add(cb);
        }
        _updatingLanguageUi = false;
    }

    private List<OcrLanguage> SelectedLanguages()
        => _langChecks.Where(kv => kv.Value.IsEnabled && kv.Value.IsChecked == true)
                      .Select(kv => kv.Key).ToList();

    private void Language_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingLanguageUi) return;
        _settings.Languages = SelectedLanguages().Select(l => l.ToString()).ToList();
        SettingsService.Save(_settings);
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

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var doc = ExportableDocument();
        if (doc == null) return;
        string text = doc.FullText();
        if (CopyToClipboard(text)) SetStatus("Copied " + text.Length + " characters");
        else SetStatus("Could not access the clipboard.");
    }

    private void SaveTxt_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("txt");
    private void SaveDocx_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("docx");
    private void SavePdf_Click(object sender, RoutedEventArgs e) => _ = ExportAsync("pdf");

    private async Task ExportAsync(string kind)
    {
        var doc = ExportableDocument();
        if (doc == null) return;
        _exportRunning++; // blocks Clear queue until the export (and the file picker) is finished
        UpdateButtons();
        try
        {
            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.SuggestedFileName = IOPath.GetFileNameWithoutExtension(doc.Name);
            string label = kind == "txt" ? "Text file" : kind == "docx" ? "Word document" : "Searchable PDF";
            picker.FileTypeChoices.Add(label, new List<string> { "." + kind });
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file == null) return;

            // Snapshot the edited lines on the UI thread, then write on a worker thread.
            var pages = doc.Pages.Select(p => (Path: p.ImagePath, Page: p.ToEditedPage())).ToList();
            string path = file.Path;

            SetStatus("Saving...");
            BusyBar.Visibility = Visibility.Visible;
            await Task.Run(() =>
            {
                switch (kind)
                {
                    case "txt": TextExporter.Write(pages.Select(p => p.Page), path); break;
                    case "docx": DocxExporter.Write(pages.Select(p => p.Page), path); break;
                    // null: Core looks in <exe dir>\fonts first, where the bundled Noto fonts are copied.
                    default: SearchablePdfExporter.Write(DecodePages(pages), path, null); break;
                }
            });
            SetStatus("Saved " + file.Name);
        }
        catch (Exception ex)
        {
            AppLog.Error("Export " + kind, ex);
            SetStatus("Could not save the file: " + DescribeError(ex));
        }
        finally
        {
            _exportRunning--;
            BusyBar.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
    }

    /// <summary>
    /// Decodes cached page images lazily, one at a time. The exporter consumes the sequence page by
    /// page, so only one full-resolution bitmap exists at any moment; each is disposed when the
    /// exporter asks for the next page.
    /// </summary>
    private static IEnumerable<(SKBitmap Image, OcrPage Page)> DecodePages(List<(string Path, OcrPage Page)> pages)
    {
        foreach (var (path, page) in pages)
        {
            using var bitmap = SKBitmap.Decode(path) ?? throw new IOException("Could not read a cached page image.");
            yield return (bitmap, page);
        }
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
            try
            {
                var engine = await GetEngineAsync();
                text = await Task.Run(() => engine.Recognize(cropForOcr, langs).Text);
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
                string msg = "Copied " + text.Length + " characters";
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
