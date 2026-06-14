using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

/// <summary>
/// ファイルプレビューを表示するフローティングウィンドウ。
/// 画像・動画・テキスト・ローディングを同じ固定サイズのボックスで表示。
/// 動画には再生/停止ボタン・シークバー・再生時間を表示。
/// フォルダ/zip由来のプレビューは左右ナビゲーションボタン+キーボード操作で切り替え可能。
/// </summary>
public class PreviewPopupWindow : Window
{
    private readonly PreviewSettings _settings;
    private readonly ContentControl _mediaHost;
    private readonly TextBlock _infoText;
    private MediaElement? _media;

    // 動画コントロール用
    private readonly DispatcherTimer _posTimer;
    private TextBlock? _timeLabel;
    private Slider?    _seekBar;
    private Button?    _playBtn;
    private bool       _isPlaying;
    private bool       _seekDragging;
    private bool       _mediaHasVideo;

    // ナビゲーション用
    private Grid? _navRow;
    private TextBlock? _navLabel;
    private List<string>? _folderItems;
    private int _folderIndex;
    private Grid? _controlPanel;

    /// <summary>左右ナビゲーション要求コールバック（HoverServiceが設定）</summary>
    public Action<List<string>, int>? OnNavigateRequest { get; set; }

    private double ContentWidth  => Math.Max(_settings.ImageMaxWidth,  _settings.VideoMaxWidth);
    private double ContentHeight => Math.Max(_settings.ImageMaxHeight, _settings.VideoMaxHeight);

    public PreviewPopupWindow(PreviewSettings settings)
    {
        _settings = settings;

        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;
        Topmost            = true;
        ResizeMode         = ResizeMode.NoResize;
        SizeToContent      = SizeToContent.WidthAndHeight;
        Left = -9999; Top = -9999;

        // キーボードイベント受け取りのため Focusable は既定 true のまま
        KeyDown += OnWindowKeyDown;

        var border = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(245, 32, 32, 32)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6),
            Opacity         = settings.OpacityPercent / 100.0,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row0: メディア
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row1: 動画コントロール
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row2: ナビゲーション
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // row3: 情報テキスト

        _mediaHost = new ContentControl
        {
            Width  = ContentWidth,
            Height = ContentHeight,
        };
        Grid.SetRow(_mediaHost, 0);

        var controlRow = BuildControlRow();
        Grid.SetRow(controlRow, 1);

        var navRow = BuildNavRow();
        Grid.SetRow(navRow, 2);

        _infoText = new TextBlock
        {
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            Margin       = new Thickness(2, 4, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth     = ContentWidth,
        };
        Grid.SetRow(_infoText, 3);

        root.Children.Add(_mediaHost);
        root.Children.Add(controlRow);
        root.Children.Add(navRow);
        root.Children.Add(_infoText);
        border.Child = root;
        Content = border;

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += OnPosTimerTick;

        SourceInitialized += OnSourceInitialized;
    }

    // ── Win32 初期化 ────────────────────────────────────────────────
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd    = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        // WS_EX_NOACTIVATE: フォーカスを奪わない
        // WS_EX_TOOLWINDOW: タスクバーに表示しない
        // WS_EX_TRANSPARENT は付けない（ボタンのクリックが通らなくなる）
        SetWindowLong(hwnd, GWL_EXSTYLE,
            exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ── 動画コントロール行 ────────────────────────────────────────────
    private UIElement BuildControlRow()
    {
        var panel = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin     = new Thickness(0, 4, 0, 0),
            Width      = ContentWidth,
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _playBtn = new Button
        {
            Content = "▶", Width = 28, Height = 22, FontSize = 11,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, IsHitTestVisible = true, Focusable = true,
        };
        _playBtn.Click += OnPlayBtnClick;
        Grid.SetColumn(_playBtn, 0);

        _seekBar = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand, IsHitTestVisible = true, Focusable = true,
            IsMoveToPointEnabled = true,
        };
        _seekBar.PreviewMouseDown += (_, _) => { _seekDragging = true; };
        _seekBar.PreviewMouseUp   += (_, _) =>
        {
            _seekDragging = false;
            if (_media != null && _media.NaturalDuration.HasTimeSpan)
                _media.Position = TimeSpan.FromSeconds(
                    _seekBar.Value * _media.NaturalDuration.TimeSpan.TotalSeconds);
        };
        _seekBar.ValueChanged += (_, _) =>
        {
            if (!_seekDragging) return;
            if (_media != null && _media.NaturalDuration.HasTimeSpan)
                _media.Position = TimeSpan.FromSeconds(
                    _seekBar.Value * _media.NaturalDuration.TimeSpan.TotalSeconds);
        };
        Grid.SetColumn(_seekBar, 1);

        _timeLabel = new TextBlock
        {
            Text = "0:00 / 0:00", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80, TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(_timeLabel, 2);

        panel.Children.Add(_playBtn);
        panel.Children.Add(_seekBar);
        panel.Children.Add(_timeLabel);

        _controlPanel = panel;
        return panel;
    }

    // ── ナビゲーション行（左右ボタン＋カウンター） ────────────────────
    private UIElement BuildNavRow()
    {
        var panel = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin     = new Thickness(0, 4, 0, 0),
            Width      = ContentWidth,
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ◀
        panel.ColumnDefinitions.Add(new ColumnDefinition());                           // カウンター
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // ▶

        var prevBtn = MakeNavButton("◀");
        prevBtn.Click += (_, _) => Navigate(-1);
        Grid.SetColumn(prevBtn, 0);

        _navLabel = new TextBlock
        {
            Text = "", FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        Grid.SetColumn(_navLabel, 1);

        var nextBtn = MakeNavButton("▶");
        nextBtn.Click += (_, _) => Navigate(+1);
        Grid.SetColumn(nextBtn, 2);

        panel.Children.Add(prevBtn);
        panel.Children.Add(_navLabel);
        panel.Children.Add(nextBtn);

        _navRow = panel;
        return panel;
    }

    private static Button MakeNavButton(string label) => new()
    {
        Content = label, Width = 32, Height = 22, FontSize = 11,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand, IsHitTestVisible = true, Focusable = false,
    };

    private void Navigate(int delta)
    {
        if (_folderItems == null || _folderItems.Count == 0) return;
        int next = (_folderIndex + delta + _folderItems.Count) % _folderItems.Count;
        OnNavigateRequest?.Invoke(_folderItems, next);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_folderItems == null || _folderItems.Count == 0) return;
        if (e.Key == Key.Left)  { Navigate(-1); e.Handled = true; }
        if (e.Key == Key.Right) { Navigate(+1); e.Handled = true; }
    }

    // ── Now Loading ──────────────────────────────────────────────────
    public void ShowLoading(string filePath, double screenX, double screenY)
    {
        StopMedia();
        _infoText.Text = "";
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;
        if (_navRow != null) _navRow.Visibility = Visibility.Collapsed;

        string fileName = System.IO.Path.GetFileName(filePath);
        var panel = new Grid { Width = ContentWidth, Height = ContentHeight };
        var inner = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        inner.Children.Add(new TextBlock
        {
            Text = "⏳", FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        inner.Children.Add(new TextBlock
        {
            Text = "Now Loading...", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        inner.Children.Add(new TextBlock
        {
            Text = fileName, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ContentWidth - 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        });
        panel.Children.Add(inner);
        PlaceContent(panel, screenX, screenY);
    }

    // ── 実コンテンツ表示 ──────────────────────────────────────────────
    public void ShowPreview(Services.PreviewInfo info, double screenX, double screenY)
    {
        StopMedia();

        // コントロールパネルはいったん非表示（BuildVideoが_mediaHasVideoを正しくセットした後で更新）
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;

        // ナビゲーション情報を更新
        _folderItems = info.FolderItems;
        _folderIndex = info.FolderIndex;
        UpdateNavRow();

        UIElement? content = info.Kind switch
        {
            Services.PreviewKind.Image => BuildImage(info),
            Services.PreviewKind.Video => BuildVideo(info),
            Services.PreviewKind.Audio => BuildAudio(info),
            Services.PreviewKind.Text  => BuildText(info),
            _                          => null,
        };

        if (content == null) { Hide(); return; }

        _infoText.Text = BuildInfoText(info);

        // コンテンツビルド後に_mediaHasVideoが確定しているので、コントロール表示を更新
        if (_controlPanel != null)
            _controlPanel.Visibility = (_media != null && _mediaHasVideo)
                ? Visibility.Visible : Visibility.Collapsed;

        PlaceContent(content, screenX, screenY);

        if (_media != null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _media?.Play();
                _isPlaying = true;
                if (_playBtn != null) _playBtn.Content = "❙❙";
                _posTimer.Start();
            }, DispatcherPriority.Loaded);
        }
    }

    private void UpdateNavRow()
    {
        if (_navRow == null || _navLabel == null) return;

        if (_folderItems != null && _folderItems.Count > 1)
        {
            _navLabel.Text = $"{_folderIndex + 1} / {_folderItems.Count}";
            _navRow.Visibility = Visibility.Visible;
        }
        else
        {
            _navRow.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaceContent(UIElement content, double screenX, double screenY)
    {
        _mediaHost.Content = content;
        Left = screenX;
        Top  = screenY;
        if (!IsVisible) Show();
        UpdateLayout();
        ClampToWorkArea(screenX, screenY);
    }

    private void ClampToWorkArea(double screenX, double screenY)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        try
        {
            var pt = new NativeMethods.POINT { X = (int)screenX, Y = (int)screenY };
            IntPtr hMon = NativeMethodsExtra.MonitorFromPoint(pt, NativeMethodsExtra.MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return;

            var mi = new NativeMethodsExtra.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethodsExtra.MONITORINFO>()
            };
            if (!NativeMethodsExtra.GetMonitorInfo(hMon, ref mi)) return;

            double left = screenX, top = screenY;
            const int margin = 4;

            if (left + w > mi.rcWork.Right)  left = mi.rcWork.Right  - w - margin;
            if (top  + h > mi.rcWork.Bottom) top  = mi.rcWork.Bottom - h - margin;
            if (left < mi.rcWork.Left) left = mi.rcWork.Left + margin;
            if (top  < mi.rcWork.Top)  top  = mi.rcWork.Top  + margin;

            if (Math.Abs(left - Left) > 0.5) Left = left;
            if (Math.Abs(top  - Top)  > 0.5) Top  = top;
        }
        catch { }
    }

    // ── 再生/停止ボタン ──────────────────────────────────────────────
    private void OnPlayBtnClick(object sender, RoutedEventArgs e)
    {
        if (_media == null) return;
        if (_isPlaying)
        {
            _media.Pause(); _isPlaying = false;
            if (_playBtn != null) _playBtn.Content = "▶";
            _posTimer.Stop();
        }
        else
        {
            _media.Play(); _isPlaying = true;
            if (_playBtn != null) _playBtn.Content = "❙❙";
            _posTimer.Start();
        }
    }

    private void OnPosTimerTick(object? sender, EventArgs e)
    {
        if (_media == null || _seekDragging) return;
        try
        {
            double pos = _media.Position.TotalSeconds;
            double dur = _media.NaturalDuration.HasTimeSpan
                ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (_seekBar != null && dur > 0) _seekBar.Value = pos / dur;
            if (_timeLabel != null) _timeLabel.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
        }
        catch { }
    }

    private static string FormatTime(double s)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // ── コンテンツビルダー ────────────────────────────────────────────
    private UIElement BuildImage(Services.PreviewInfo info)
    {
        _mediaHasVideo = false;
        if (info.Image == null) return MakeErrorBox("画像を読み込めませんでした");
        double iw = info.Image.PixelWidth, ih = info.Image.PixelHeight;
        if (iw <= 0 || ih <= 0) return MakeErrorBox("画像サイズ不明");
        double scale = Math.Min(Math.Min(ContentWidth / iw, ContentHeight / ih), 1.0);
        var grid = new Grid
        {
            Width = ContentWidth, Height = ContentHeight,
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
        };
        grid.Children.Add(new System.Windows.Controls.Image
        {
            Source = info.Image,
            Width  = Math.Floor(iw * scale), Height = Math.Floor(ih * scale),
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        });
        return grid;
    }

    private UIElement? BuildVideo(Services.PreviewInfo info)
    {
        if (string.IsNullOrEmpty(info.TempMediaPath)) { _mediaHasVideo = false; return null; }
        _mediaHasVideo = true;

        _media = new MediaElement
        {
            Source = new Uri(info.TempMediaPath, UriKind.Absolute),
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform, Volume = _settings.PlaybackVolume / 100.0,
            Width = ContentWidth, Height = ContentHeight,
        };
        var capturedInfo = info;
        _media.MediaOpened += (_, _) =>
        {
            if (_media == null) return;
            double dur = _media.NaturalDuration.HasTimeSpan
                ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (_timeLabel != null) _timeLabel.Text = $"0:00 / {FormatTime(dur)}";
            if (dur > 0)
            {
                capturedInfo.Duration = TimeSpan.FromSeconds(dur);
                _infoText.Text = BuildInfoText(capturedInfo);
            }
        };
        _media.MediaFailed += (_, _) => { _mediaHasVideo = false; };
        if (_settings.RememberPlaybackPosition &&
            _settings.PlaybackPositions.TryGetValue(info.DisplayPath, out double pos))
            _media.Position = TimeSpan.FromSeconds(pos);
        return _media;
    }

    private UIElement? BuildAudio(Services.PreviewInfo info)
    {
        if (string.IsNullOrEmpty(info.TempMediaPath)) { _mediaHasVideo = false; return null; }
        _mediaHasVideo = false;
        _media = new MediaElement
        {
            Source = new Uri(info.TempMediaPath, UriKind.Absolute),
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Volume = _settings.PlaybackVolume / 100.0, Width = 0, Height = 0,
        };
        if (_settings.RememberPlaybackPosition &&
            _settings.PlaybackPositions.TryGetValue(info.DisplayPath, out double pos))
            _media.Position = TimeSpan.FromSeconds(pos);

        var grid = new Grid { Width = ContentWidth, Height = ContentHeight };
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "🎵", FontSize = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "オーディオ再生中...", FontSize = 12,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(_media);
        grid.Children.Add(panel);
        return grid;
    }

    private UIElement BuildText(Services.PreviewInfo info)
    {
        _mediaHasVideo = false;
        var fg = ParseColor(_settings.TextForegroundColor, Color.FromRgb(224, 224, 224));
        var bg = ParseColor(_settings.TextBackgroundColor, Color.FromRgb(32, 32, 32));
        return new TextBox
        {
            Text = info.TextContent, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(_settings.TextFontFamily), FontSize = _settings.TextFontSize,
            Foreground = new SolidColorBrush(fg), Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(0),
            Width = _settings.TextMaxWidth, Height = _settings.TextMaxHeight,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private UIElement MakeErrorBox(string msg)
    {
        var grid = new Grid { Width = ContentWidth, Height = ContentHeight };
        grid.Children.Add(new TextBlock
        {
            Text = msg, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        });
        return grid;
    }

    // ── 情報テキスト ──────────────────────────────────────────────────
    private string BuildInfoText(Services.PreviewInfo info)
    {
        var parts = new List<string> { info.FileName };
        if (info.SizeBytes > 0) parts.Add(FormatSize(info.SizeBytes));
        if (info.Modified.HasValue) parts.Add(info.Modified.Value.ToString("yyyy/MM/dd HH:mm"));
        if (!string.IsNullOrEmpty(info.Dimensions)) parts.Add(info.Dimensions);
        if (info.Duration.HasValue) parts.Add("🎬 " + FormatTime(info.Duration.Value.TotalSeconds));
        if (info.IsArchiveEntry) parts.Add("📦 圧縮内");
        if (info.IsFolderItem && !info.IsArchiveEntry) parts.Add("📁 フォルダ内");
        if (info.IsNetworkPath) parts.Add("🌐 ネットワーク");
        return string.Join("  |  ", parts);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes; int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return fallback;
            return Color.FromRgb(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        catch { return fallback; }
    }

    // ── 公開メソッド ─────────────────────────────────────────────────
    public double? GetCurrentMediaPositionSeconds()
    {
        try { return _media?.Position.TotalSeconds; } catch { return null; }
    }

    public void StopMedia()
    {
        _posTimer.Stop();
        _isPlaying = false; _seekDragging = false; _mediaHasVideo = false;
        if (_playBtn  != null) _playBtn.Content  = "▶";
        if (_seekBar  != null) _seekBar.Value    = 0;
        if (_timeLabel != null) _timeLabel.Text  = "0:00 / 0:00";
        try
        {
            if (_media != null) { _media.Stop(); _media.Source = null; _media = null; }
        }
        catch { }
    }

    public new void Hide()
    {
        StopMedia();
        _mediaHost.Content = null;
        _infoText.Text = "";
        _folderItems = null;
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;
        if (_navRow != null) _navRow.Visibility = Visibility.Collapsed;
        base.Hide();
    }
}
