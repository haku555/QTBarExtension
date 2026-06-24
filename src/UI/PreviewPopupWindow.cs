using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

public class PreviewPopupWindow : Window
{
    // WM_MOUSEACTIVATE 関連定数
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const IntPtr MA_NOACTIVATE = (nint)3;

    // Win32フォーカス操作（WS_EX_NOACTIVATE環境でのTextBox選択に必要）
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);

    private readonly PreviewSettings _settings;
    private readonly ContentControl _mediaHost;
    private readonly TextBlock _infoText;
    private MediaElement? _media;
    private TextBox? _textBox;  // テキストプレビュー用TextBox参照
    private IntPtr _popupHwnd;  // このウィンドウのHWND（SetFocus用）

    private readonly DispatcherTimer _posTimer;
    private TextBlock? _timeLabel;
    private Slider?    _seekBar;
    private Button?    _playBtn;
    private bool       _isPlaying;
    private bool       _seekDragging;
    private bool       _mediaHasVideo;

    private Grid? _navRow;
    private TextBlock? _navLabel;
    private List<string>? _folderItems;
    private int _folderIndex;
    private Grid? _controlPanel;

    // アニメーションWebP再生用
    private DispatcherTimer? _animTimer;
    private System.Windows.Controls.Image? _animImage;
    private BitmapFrame[]? _animFrames;
    private int[] _animDelaysMs = [];
    private int _animFrameIndex;
    private bool _animPlaying;

    // ルートBorderへの参照（テーマ再適用用）
    private readonly Border _rootBorder;

    public Action<List<string>, int>? OnNavigateRequest { get; set; }

    private double ContentWidth  => Math.Max(_settings.ImageMaxWidth,  _settings.VideoMaxWidth);
    private double ContentHeight => Math.Max(_settings.ImageMaxHeight, _settings.VideoMaxHeight);

    // 画像/動画のアスペクト比に合わせて縮小した際、シークバー等のコントロールが
    // 潰れすぎないようにする最小辺長（px）
    private const double MinContentSide = 80;

    // ── テーマ判定 ────────────────────────────────────────────────
    private bool IsDark => _settings.PreviewTheme == "dark" ||
        (_settings.PreviewTheme == "system" && IsSystemDark());

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { }
        return false;
    }

    // ── テーマカラー ──────────────────────────────────────────────
    private Color BgColor       => IsDark ? Color.FromArgb(245, 32,  32,  32)  : Color.FromArgb(245, 245, 245, 245);
    private Color BorderColor   => IsDark ? Color.FromRgb(90,  90,  90)        : Color.FromRgb(180, 180, 180);
    private Color InfoTextColor => IsDark ? Color.FromRgb(220, 220, 220)       : Color.FromRgb(50,  50,  50);
    private Color ImageBgColor  => IsDark ? Color.FromRgb(20,  20,  20)        : Color.FromRgb(220, 220, 220);
    private Color LoadingIconColor   => IsDark ? Color.FromRgb(180, 180, 180)  : Color.FromRgb(100, 100, 100);
    private Color LoadingTitleColor  => IsDark ? Color.FromRgb(200, 200, 200)  : Color.FromRgb(60,  60,  60);
    private Color LoadingSubColor    => IsDark ? Color.FromRgb(140, 140, 140)  : Color.FromRgb(120, 120, 120);
    private Color CtrlBtnBg     => IsDark ? Color.FromRgb(60,  60,  60)        : Color.FromRgb(210, 210, 210);
    private Color CtrlBtnBorder => IsDark ? Color.FromRgb(100, 100, 100)       : Color.FromRgb(160, 160, 160);
    private Color CtrlBtnFg     => IsDark ? Colors.White                        : Color.FromRgb(30,  30,  30);
    private Color TimeLabelColor => IsDark ? Color.FromRgb(200, 200, 200)      : Color.FromRgb(70,  70,  70);
    private Color NavLabelColor  => IsDark ? Color.FromRgb(180, 180, 180)      : Color.FromRgb(80,  80,  80);
    private Color AudioTextColor => IsDark ? Colors.White                       : Color.FromRgb(30,  30,  30);
    private Color ErrorTextColor => IsDark ? Color.FromRgb(160, 160, 160)      : Color.FromRgb(100, 100, 100);

    // テキストプレビュー色（設定値があれば使い、"auto"/"system"なら自動）
    private Color TextFgColor => (_settings.TextForegroundColor == "auto" || string.IsNullOrEmpty(_settings.TextForegroundColor))
        ? (IsDark ? Color.FromRgb(224, 224, 224) : Color.FromRgb(30,  30,  30))
        : ParseColor(_settings.TextForegroundColor, IsDark ? Color.FromRgb(224, 224, 224) : Color.FromRgb(30, 30, 30));
    private Color TextBgColor => (_settings.TextBackgroundColor == "auto" || string.IsNullOrEmpty(_settings.TextBackgroundColor))
        ? (IsDark ? Color.FromRgb(32,  32,  32)  : Color.FromRgb(250, 250, 250))
        : ParseColor(_settings.TextBackgroundColor, IsDark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(250, 250, 250));

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

        KeyDown += OnWindowKeyDown;

        _rootBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6),
            Opacity         = settings.OpacityPercent / 100.0,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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
            Margin       = new Thickness(2, 4, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth     = ContentWidth,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        Grid.SetRow(_infoText, 3);

        root.Children.Add(_mediaHost);
        root.Children.Add(controlRow);
        root.Children.Add(navRow);
        root.Children.Add(_infoText);
        _rootBorder.Child = root;
        Content = _rootBorder;

        _posTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += OnPosTimerTick;

        SourceInitialized += OnSourceInitialized;
    }

    // テーマカラーをルートBorderとinfoTextに適用
    private void ApplyTheme()
    {
        _rootBorder.Background  = new SolidColorBrush(BgColor);
        _rootBorder.BorderBrush = new SolidColorBrush(BorderColor);
        if (_infoText != null)
            _infoText.Foreground = new SolidColorBrush(InfoTextColor);
        // 動画コントロールの色更新
        ApplyControlTheme();
    }

    private void ApplyControlTheme()
    {
        if (_playBtn != null)
        {
            _playBtn.Background  = new SolidColorBrush(CtrlBtnBg);
            _playBtn.Foreground  = new SolidColorBrush(CtrlBtnFg);
            _playBtn.BorderBrush = new SolidColorBrush(CtrlBtnBorder);
        }
        if (_timeLabel != null)
            _timeLabel.Foreground = new SolidColorBrush(TimeLabelColor);
        if (_navLabel != null)
            _navLabel.Foreground = new SolidColorBrush(NavLabelColor);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyTheme();
        _popupHwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(_popupHwnd, GWL_EXSTYLE);
        SetWindowLong(_popupHwnd, GWL_EXSTYLE,
            exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // WM_MOUSEACTIVATE に MA_NOACTIVATE を返す → マウスイベントを受け取りつつ
        // Explorerウィンドウのアクティブ状態を奪わない。
        HwndSource.FromHwnd(_popupHwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_LBUTTONDOWN = 0x0201;

        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }

        // テキストプレビュー表示中にクリックされたとき：
        // WndProcはUIスレッドで動くため AttachThreadInput は不要。
        // SetFocus(hwnd) を同一スレッドから直接呼ぶことで Explorerスレッドに
        // 一切干渉せずにWin32フォーカスをこのウィンドウへ移せる。
        if (msg == WM_LBUTTONDOWN && _textBox != null)
        {
            try
            {
                SetFocus(hwnd);
                Dispatcher.BeginInvoke(() => _textBox?.Focus(), DispatcherPriority.Input);
            }
            catch { }
        }

        return IntPtr.Zero;
    }

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
            if (_animFrames != null)
            {
                _animFrameIndex = (int)Math.Round(_seekBar.Value);
                if (_animImage != null) _animImage.Source = _animFrames[_animFrameIndex];
                if (_timeLabel != null) _timeLabel.Text = $"{_animFrameIndex + 1} / {_animFrames.Length}";
            }
            else if (_media != null && _media.NaturalDuration.HasTimeSpan)
                _media.Position = TimeSpan.FromSeconds(
                    _seekBar.Value * _media.NaturalDuration.TimeSpan.TotalSeconds);
        };
        _seekBar.ValueChanged += (_, _) =>
        {
            if (!_seekDragging) return;
            if (_animFrames != null)
            {
                int fi = (int)Math.Round(_seekBar.Value);
                if (_animImage != null && fi < _animFrames.Length) _animImage.Source = _animFrames[fi];
                if (_timeLabel != null) _timeLabel.Text = $"{fi + 1} / {_animFrames.Length}";
            }
            else if (_media != null && _media.NaturalDuration.HasTimeSpan)
                _media.Position = TimeSpan.FromSeconds(
                    _seekBar.Value * _media.NaturalDuration.TimeSpan.TotalSeconds);
        };
        Grid.SetColumn(_seekBar, 1);

        _timeLabel = new TextBlock
        {
            Text = "0:00 / 0:00", FontSize = 10,
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
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prevBtn = MakeNavButton("◀");
        prevBtn.Click += (_, _) => Navigate(-1);
        Grid.SetColumn(prevBtn, 0);

        _navLabel = new TextBlock
        {
            Text = "", FontSize = 10,
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

    private Button MakeNavButton(string label) => new()
    {
        Content = label, Width = 32, Height = 22, FontSize = 11,
        Padding = new Thickness(0),
        Background  = new SolidColorBrush(CtrlBtnBg),
        Foreground  = new SolidColorBrush(CtrlBtnFg),
        BorderBrush = new SolidColorBrush(CtrlBtnBorder),
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

    public void ShowLoading(string filePath, double screenX, double screenY)
    {
        _mediaHost.Content = null;  // 前の画像が残って見えるのを防ぐ
        StopMedia();
        ApplyTheme();
        _infoText.Text = "";
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;
        if (_navRow != null) _navRow.Visibility = Visibility.Collapsed;
        // 前回表示分でアスペクト比に合わせて縮小されている可能性があるため既定サイズへ戻す
        SetContentSize(ContentWidth, ContentHeight);

        string fileName = System.IO.Path.GetFileName(filePath);
        var panel = new Grid { Width = ContentWidth, Height = ContentHeight,
            Background = new SolidColorBrush(ImageBgColor) };
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
            Foreground = new SolidColorBrush(LoadingIconColor),
            Margin = new Thickness(0, 0, 0, 6),
        });
        inner.Children.Add(new TextBlock
        {
            Text = "Now Loading...", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(LoadingTitleColor),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        inner.Children.Add(new TextBlock
        {
            Text = fileName, FontSize = 10,
            Foreground = new SolidColorBrush(LoadingSubColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ContentWidth - 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        });
        panel.Children.Add(inner);
        PlaceContent(panel, screenX, screenY);
    }

    public void ShowPreview(Services.PreviewInfo info, double screenX, double screenY)
    {
        // 古いコンテンツを即時クリア（前の画像が一瞬残って見えるのを防ぐ）
        _mediaHost.Content = null;
        StopMedia();
        ApplyTheme();
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;
        // 前回表示分でアスペクト比に合わせて縮小されている可能性があるため既定サイズへ戻す。
        // 画像/動画の場合はBuildImage/BuildVideo(MediaOpened)が後で実サイズに上書きする。
        SetContentSize(ContentWidth, ContentHeight);

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

        if (_controlPanel != null)
            _controlPanel.Visibility = ((_media != null || _animTimer != null) && _mediaHasVideo)
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
        else if (_animTimer != null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _animPlaying = true;
                _animTimer?.Start();
                _isPlaying = true;
                if (_playBtn != null) _playBtn.Content = "❙❙";
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

    // 画像/動画の実表示サイズ(余白なし)に合わせて、メディアホストおよび関連UI行の幅・高さを揃える。
    // 横幅はコントロール行・ナビ行・情報テキストの折返し幅にも反映し、細い画像でも
    // 不自然に幅広いバーが残らないようにする。
    private void SetContentSize(double width, double height)
    {
        _mediaHost.Width  = width;
        _mediaHost.Height = height;
        if (_controlPanel != null) _controlPanel.Width = width;
        if (_navRow != null) _navRow.Width = width;
        _infoText.MaxWidth = width;
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
        double w = ActualWidth, h = ActualHeight;
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

    private void OnPlayBtnClick(object sender, RoutedEventArgs e)
    {
        // アニメーションWebP
        if (_animTimer != null)
        {
            if (_animPlaying)
            {
                _animTimer.Stop(); _animPlaying = false; _isPlaying = false;
                if (_playBtn != null) _playBtn.Content = "▶";
            }
            else
            {
                _animPlaying = true; _isPlaying = true;
                _animTimer.Start();
                if (_playBtn != null) _playBtn.Content = "❙❙";
            }
            return;
        }
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

    private UIElement BuildImage(Services.PreviewInfo info)
    {
        _mediaHasVideo = false;
        if (info.Image == null) return MakeErrorBox("画像を読み込めませんでした");
        double iw = info.Image.PixelWidth, ih = info.Image.PixelHeight;
        if (iw <= 0 || ih <= 0) return MakeErrorBox("画像サイズ不明");
        double scale = Math.Min(Math.Min(ContentWidth / iw, ContentHeight / ih), 1.0);
        // 余白が出ないよう、Grid自体をスケール後の実サイズに合わせる（最小サイズのみ下支え）
        double dispW = Math.Max(Math.Floor(iw * scale), MinContentSide);
        double dispH = Math.Max(Math.Floor(ih * scale), MinContentSide);
        var grid = new Grid
        {
            Width = dispW, Height = dispH,
            Background = new SolidColorBrush(ImageBgColor),
        };
        var imageElement = new System.Windows.Controls.Image
        {
            Source = info.Image,
            Width  = dispW, Height = dispH,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        // 縮小時のジャギー対策：高品質スケーリング（Fant法相当）を明示。
        // SnapsToDevicePixelsは縮小描画ではかえってギザつきの原因になるため付与しない。
        RenderOptions.SetBitmapScalingMode(imageElement, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(imageElement, EdgeMode.Unspecified);
        grid.Children.Add(imageElement);
        SetContentSize(dispW, dispH);
        return grid;
    }

    private UIElement? BuildVideo(Services.PreviewInfo info)
    {
        if (string.IsNullOrEmpty(info.TempMediaPath)) { _mediaHasVideo = false; return null; }

        // アニメーションWebPはMediaElementで再生不可 → フレーム切り替えアニメーション
        if (info.IsAnimatedWebp)
            return BuildAnimatedWebp(info);

        // 通常動画: シークバーを0-1比率モードに戻す
        if (_seekBar != null) { _seekBar.Minimum = 0; _seekBar.Maximum = 1; _seekBar.Value = 0; }

        _mediaHasVideo = true;
        _media = new MediaElement
        {
            Source = new Uri(info.TempMediaPath, UriKind.Absolute),
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform, Volume = _settings.PlaybackVolume / 100.0,
            Width = ContentWidth, Height = ContentHeight,
        };
        // 縮小表示時のジャギー対策（WPF合成レイヤーでのリサイズ品質を改善）
        RenderOptions.SetBitmapScalingMode(_media, BitmapScalingMode.HighQuality);
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
            // 実解像度が判明した時点でアスペクト比に合わせてリサイズ（余白を排除）
            ResizeVideoToNaturalAspect();
        };
        _media.MediaFailed += (_, e) =>
        {
            _mediaHasVideo = false;
            string ext = System.IO.Path.GetExtension(info.TempMediaPath ?? "").ToUpperInvariant();
            string msg = $"{ext} を再生できません\nコーデックがインストールされていない可能性があります";
            _mediaHost.Content = MakeErrorBox(msg);
        };
        if (_settings.RememberPlaybackPosition &&
            _settings.PlaybackPositions.TryGetValue(info.DisplayPath, out double pos))
            _media.Position = TimeSpan.FromSeconds(pos);
        return _media;
    }

    // 動画の実解像度（NaturalVideoWidth/Height）に基づき、_mediaHost・MediaElement・
    // 関連行の幅を再計算する。MediaOpenedは非同期なので初期表示時はContentWidth/Heightの
    // 仮サイズで開始し、判明時点でこのメソッドにより余白なしの実サイズへ更新する。
    private void ResizeVideoToNaturalAspect()
    {
        if (_media == null) return;
        int vw = _media.NaturalVideoWidth, vh = _media.NaturalVideoHeight;
        if (vw <= 0 || vh <= 0) return;

        double scale = Math.Min(Math.Min(ContentWidth / vw, ContentHeight / vh), 1.0);
        double dispW = Math.Max(Math.Floor(vw * scale), MinContentSide);
        double dispH = Math.Max(Math.Floor(vh * scale), MinContentSide);

        _media.Width  = dispW;
        _media.Height = dispH;
        // ナビゲーション等で既に別のメディアに差し替わっている場合は反映しない
        if (_mediaHost.Content != _media) return;
        SetContentSize(dispW, dispH);

        // ウィンドウは SizeToContent のため、再レイアウト後に作業領域内へクランプし直す
        UpdateLayout();
        ClampToWorkArea(Left, Top);
    }

    // ── アニメーションWebP再生 ───────────────────────────────────────
    private UIElement? BuildAnimatedWebp(Services.PreviewInfo info)
    {
        _mediaHasVideo = false;
        StopAnimWebp();

        BitmapFrame[]? frames = null;
        int[]? delays = null;
        int frameW = 0, frameH = 0;

        try
        {
            byte[] webpBytes = System.IO.File.ReadAllBytes(info.TempMediaPath!);
            using var ms = new System.IO.MemoryStream(webpBytes);
            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return MakeErrorBox("フレームを読み込めませんでした");

            frames = new BitmapFrame[decoder.Frames.Count];
            delays = new int[decoder.Frames.Count];
            for (int i = 0; i < decoder.Frames.Count; i++)
            {
                var f = decoder.Frames[i];
                f.Freeze();
                frames[i] = f;
                // フレーム遅延はメタデータから取得（WebPはミリ秒単位）
                // BitmapFrame.Metadataが取れない場合は100ms（10fps相当）をデフォルトに
                int delayMs = 100;
                try
                {
                    if (f.Metadata is BitmapMetadata meta)
                    {
                        // WebPメタデータのパスはデコーダ依存
                        // GIFと同様 /grctlext/Delay を試みる（単位:10ms）
                        object? delayObj = null;
                        try { delayObj = meta.GetQuery("/grctlext/Delay"); } catch { }
                        if (delayObj is ushort gifDelay && gifDelay > 0)
                            delayMs = gifDelay * 10;
                    }
                }
                catch { }
                delays[i] = Math.Max(delayMs, 20); // 最低20ms
                if (i == 0) { frameW = f.PixelWidth; frameH = f.PixelHeight; }
            }
        }
        catch { return MakeErrorBox("アニメーションWebPを読み込めませんでした"); }

        if (frames == null || frames.Length == 0 || delays == null)
            return MakeErrorBox("フレームがありません");

        double scale = Math.Min(Math.Min(ContentWidth / frameW, ContentHeight / frameH), 1.0);
        double dispW = Math.Max(Math.Floor(frameW * scale), MinContentSide);
        double dispH = Math.Max(Math.Floor(frameH * scale), MinContentSide);

        _animImage = new System.Windows.Controls.Image
        {
            Source = frames[0],
            Width = dispW, Height = dispH,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_animImage, BitmapScalingMode.HighQuality);

        var grid = new Grid
        {
            Width = dispW, Height = dispH,
            Background = new SolidColorBrush(ImageBgColor),
        };
        grid.Children.Add(_animImage);
        SetContentSize(dispW, dispH);

        // 情報更新
        info.Dimensions = $"{frameW} x {frameH}";
        double totalMs = 0; foreach (var d in delays) totalMs += d;
        info.Duration = TimeSpan.FromMilliseconds(totalMs);
        _infoText.Text = BuildInfoText(info);

        // シークバーをフレーム数でセットアップ（コントロールパネルを動画UIと共用）
        _mediaHasVideo = true; // コントロールパネル表示フラグ
        if (_seekBar != null)
        {
            _seekBar.Minimum = 0;
            _seekBar.Maximum = Math.Max(1, frames.Length - 1);
            _seekBar.Value = 0;
        }
        if (_timeLabel != null)
            _timeLabel.Text = $"1 / {frames.Length}";

        // アニメーション状態を設定してタイマー開始
        _animFrames = frames;
        _animDelaysMs = delays;
        _animFrameIndex = 0;
        _animPlaying = false; // ShowPreviewからPlay()される

        _animTimer = new DispatcherTimer(DispatcherPriority.Render);
        _animTimer.Interval = TimeSpan.FromMilliseconds(delays[0]);
        _animTimer.Tick += OnAnimTick;

        return grid;
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_animFrames == null || _animImage == null || !_animPlaying) return;
        _animFrameIndex = (_animFrameIndex + 1) % _animFrames.Length;
        _animImage.Source = _animFrames[_animFrameIndex];
        if (_seekBar != null && !_seekDragging) _seekBar.Value = _animFrameIndex;
        if (_timeLabel != null) _timeLabel.Text = $"{_animFrameIndex + 1} / {_animFrames.Length}";
        // 次フレームの遅延を設定
        _animTimer!.Interval = TimeSpan.FromMilliseconds(_animDelaysMs[_animFrameIndex]);
    }

    private void StopAnimWebp()
    {
        _animTimer?.Stop();
        _animTimer = null;
        _animFrames = null;
        _animDelaysMs = [];
        _animFrameIndex = 0;
        _animPlaying = false;
        _animImage = null;
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

        var grid = new Grid
        {
            Width = ContentWidth, Height = ContentHeight,
            Background = new SolidColorBrush(ImageBgColor),
        };
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
            Foreground = new SolidColorBrush(AudioTextColor),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(_media);
        grid.Children.Add(panel);
        return grid;
    }

    private UIElement BuildText(Services.PreviewInfo info)
    {
        _mediaHasVideo = false;
        double tw = _settings.TextMaxWidth;
        double th = _settings.TextMaxHeight;
        SetContentSize(tw, th);

        var tb = new TextBox
        {
            Text = info.TextContent,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily(_settings.TextFontFamily),
            FontSize = _settings.TextFontSize,
            Foreground = new SolidColorBrush(TextFgColor),
            Background = new SolidColorBrush(TextBgColor),
            BorderThickness = new Thickness(0),
            Width = tw,
            Height = th,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsHitTestVisible = true,
            Focusable = true,
            Cursor = Cursors.IBeam,
            // キャレットを非表示にしてビューアらしい見た目に
            CaretBrush = Brushes.Transparent,
        };

        // WndProc の WM_LBUTTONDOWN で Win32 SetFocus + WPF Focus() を行うため、
        // ここでは e.Handled を false のまま TextBox 内部のヒットテストに委ねる。
        tb.PreviewMouseDown += (_, _) => { /* WndProc側で処理 */ };

        _textBox = tb;
        return tb;
    }

    private UIElement MakeErrorBox(string msg)
    {
        var grid = new Grid
        {
            Width = ContentWidth, Height = ContentHeight,
            Background = new SolidColorBrush(ImageBgColor),
        };
        grid.Children.Add(new TextBlock
        {
            Text = msg, FontSize = 11,
            Foreground = new SolidColorBrush(ErrorTextColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        });
        return grid;
    }

    private string BuildInfoText(Services.PreviewInfo info)
    {
        var s = _settings;
        var line1 = new List<string>();
        var line2 = new List<string>();
        var line3 = new List<string>();

        // 1行目: ファイル名 + サイズ + 拡張子
        line1.Add(info.FileName);
        if (info.SizeBytes > 0)
            line1.Add(s.ShowSizeWithTwoDecimals ? FormatSizePrecise(info.SizeBytes) : FormatSize(info.SizeBytes));
        if (s.InfoShowExtension)
        {
            string ext = System.IO.Path.GetExtension(info.FileName).ToUpperInvariant();
            if (!string.IsNullOrEmpty(ext)) line1.Add(ext);
        }

        // 2行目: 解像度 + 再生時間
        if (s.InfoShowDimensions && !string.IsNullOrEmpty(info.Dimensions))
            line2.Add("📐 " + info.Dimensions);
        if (s.InfoShowDuration && info.Duration.HasValue)
            line2.Add("🎬 " + FormatTime(info.Duration.Value.TotalSeconds));

        // 3行目: 日時 + バッジ
        if (s.InfoShowModifiedDate && info.Modified.HasValue)
            line3.Add("📅 " + info.Modified.Value.ToString("yyyy/MM/dd HH:mm"));
        if (s.InfoShowCreatedDate && info.Created.HasValue)
            line3.Add("📁作成: " + info.Created.Value.ToString("yyyy/MM/dd HH:mm"));
        if (s.InfoShowLocationBadge && info.IsArchiveEntry)
            line3.Add("📦 圧縮内");
        if (s.InfoShowLocationBadge && info.IsFolderItem && !info.IsArchiveEntry)
            line3.Add("📁 フォルダ内");
        if (s.InfoShowNetworkBadge && info.IsNetworkPath)
            line3.Add("🌐 ネットワーク");

        if (!s.InfoTextMultiLine)
        {
            // 1行モード: 全部まとめて " | " 区切り
            var all = new List<string>(line1);
            all.AddRange(line2);
            all.AddRange(line3);
            return string.Join("  |  ", all);
        }

        // 複数行モード
        var lines = new List<string>();
        if (line1.Count > 0) lines.Add(string.Join("  |  ", line1));
        if (line2.Count > 0) lines.Add(string.Join("  |  ", line2));
        if (line3.Count > 0) lines.Add(string.Join("  |  ", line3));
        return string.Join("\n", lines);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes; int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }

    private static string FormatSizePrecise(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes; int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        // B単位のときは小数不要
        return u == 0 ? $"{size:0} {units[u]}" : $"{size:0.00} {units[u]}";
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
        StopAnimWebp();
    }

    public new void Hide()
    {
        StopMedia();
        _mediaHost.Content = null;
        _textBox = null;
        _infoText.Text = "";
        _folderItems = null;
        if (_controlPanel != null) _controlPanel.Visibility = Visibility.Collapsed;
        if (_navRow != null) _navRow.Visibility = Visibility.Collapsed;
        base.Hide();
    }
}
