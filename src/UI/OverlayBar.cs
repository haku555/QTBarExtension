using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using QTBarExtension.Services;
using static QTBarExtension.Core.NativeMethods;
using MediaBrushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace QTBarExtension.UI;

public class OverlayBar : Window
{
    // ── フィールド ───────────────────────────────────────────────
    private readonly ExplorerWindowInfo _explorer;
    private readonly AppSettings        _settings;
    private readonly Action             _saveSettings;
    private readonly Action<string>     _onTabDetached;

    private readonly List<TabData> _tabs = [];
    private int  _activeIndex;
    private bool _parentSet;

    private readonly StackPanel      _tabStrip;
    private readonly Border          _rootBorder;
    private readonly DispatcherTimer _timer;
    private bool _timerRunning;

    // 現在アクティブなグループ（右クリックで開いたグループ）
    private TabGroup? _activeGroup;

    // ドラッグ状態
    private int   _dragSourceIndex = -1;
    private int    _dropTargetIndex    = -1;  // ドロップ先インジケーター位置
    private double _tabStripRightCache  = 0;  // タブ列の右端X（インジケーターを除く）
    private bool   _isFolderDragging   = false; // Explorerからのフォルダドラッグ中
    private bool  _isDragging;
    private Point _dragStartPos;

    // クロスBarドラッグ受け入れ状態（他のBarからドラッグされてきた場合）
    private bool   _isCrossBarTarget    = false;
    private int    _crossBarDropIndex   = -1;
    private double[] _crossBarTabWidths = [];
    private double _crossBarStripRight  = 0;

    // サブフォルダメニュー
    private SubFolderMenuService? _subFolderMenuService;

    public IntPtr ExplorerHwnd => _explorer.MainHwnd;

    /// <summary>サブフォルダメニューサービスを設定する（App.csから呼ぶ）</summary>
    public void SetSubFolderMenuService(SubFolderMenuService svc) => _subFolderMenuService = svc;

    // ── テーマ ────────────────────────────────────────────────────
    private bool IsDark => _settings.Theme == "dark" ||
        (_settings.Theme == "system" && IsSystemDark());

    private Color BgColor       => IsDark ? Color.FromRgb(32,  32,  32)  : Color.FromRgb(243, 243, 243);
    private Color BorderColor   => IsDark ? Color.FromRgb(60,  60,  60)  : Color.FromRgb(200, 200, 200);
    private Color TabActiveBg   => IsDark ? Color.FromRgb(55,  55,  55)  : Color.FromRgb(255, 255, 255);
    private Color TabInactiveBg => IsDark ? Color.FromRgb(38,  38,  38)  : Color.FromRgb(220, 220, 220);
    private Color TabHoverBg    => IsDark ? Color.FromRgb(50,  50,  50)  : Color.FromRgb(235, 235, 235);
    private Color TextActive    => IsDark ? Color.FromRgb(240, 240, 240) : Color.FromRgb(10,  10,  10);
    private Color TextInactive  => IsDark ? Color.FromRgb(170, 170, 170) : Color.FromRgb(80,  80,  80);
    private Color TextMuted     => IsDark ? Color.FromRgb(100, 100, 100) : Color.FromRgb(150, 150, 150);
    private static Color Accent => Color.FromRgb(0, 120, 212);

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

    // ── タブデータ ────────────────────────────────────────────────
    private class TabData
    {
        public string Url    { get; set; }
        public string Label  { get; set; } = "";
        public bool   Locked { get; set; }
        public TabData(string url) => Url = url;
        public string DisplayName => Label.Length > 0 ? Label : ShellHelper.GetDisplayName(Url);
        public string LocalPath   => ShellHelper.UrlToLocalPath(Url);
    }

    // ── コンストラクタ ────────────────────────────────────────────
    public OverlayBar(ExplorerWindowInfo explorer, AppSettings settings,
        Action saveSettings, Action<string>? onTabDetached = null)
    {
        _explorer      = explorer;
        _settings      = settings;
        _saveSettings  = saveSettings;
        _onTabDetached = onTabDetached ?? (_ => { });

        WindowStyle        = WindowStyle.None;
        AllowsTransparency = false;
        ShowInTaskbar      = false;
        ResizeMode         = ResizeMode.NoResize;
        Topmost            = false;
        Height             = Math.Max(settings.BarHeight, 30);
        Width              = 400;
        Left = Top         = -9999;

        // ── レイアウト ──────────────────────────────────────────
        _rootBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        ApplyThemeToRoot();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 右ボタン群

        // タブ帯（横スクロール）
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal };
        var tabScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            VerticalAlignment             = VerticalAlignment.Stretch,
        };
        tabScroll.PreviewMouseWheel += (_, e) =>
        {
            tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - e.Delta * 0.5);
            e.Handled = true;
        };
        Grid.SetColumn(tabScroll, 0);

        // 右ボタン群
        var btnPanel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 4, 0),
        };
        btnPanel.Children.Add(MakeIconBtn("＋", "新しいタブ（現在を複製）", DuplicateCurrentTab));
        btnPanel.Children.Add(MakeIconBtn("⊞", "タブスイッチャー",         ShowTabSwitcher));
        btnPanel.Children.Add(MakeIconBtn("🕐", "履歴",                    ShowHistory));
        btnPanel.Children.Add(MakeIconBtn("☰",  "メニュー",                ShowMenu));
        Grid.SetColumn(btnPanel, 1);

        grid.Children.Add(tabScroll);

        grid.Children.Add(btnPanel);
        _rootBorder.Child = grid;
        Content = _rootBorder;

        // バー空白右クリック
        _rootBorder.MouseRightButtonDown += (_, e) =>
        {
            ShowBarContextMenu(); e.Handled = true;
        };

        // フォルダのドラッグアンドドロップ受け入れ
        AllowDrop = true;
        Drop      += OnBarDrop;
        DragOver  += OnBarDragOver;
        DragLeave += OnBarDragLeave;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _timer.Tick += OnTimer;
        _timer.Start();

        // ※ フォルダD&Dインジケーターは RebuildTabStrip に統一（タイマー不要）

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => FirstSync();
    }

    // ── フォルダD&D受け入れ ───────────────────────────────────────
    private void OnBarDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null) return;

        // ドロップ位置を確定（DragOver中のインジケーター位置を使用）
        int insertAt = (_dropTargetIndex == CloneZoneIndex || _dropTargetIndex < 0)
            ? _tabs.Count  // 末尾に追加
            : _dropTargetIndex;

        // フォルダドラッグ状態をリセット（先にリセットしてRebuildのループを防ぐ）
        _dropTargetIndex         = -1;
        _isFolderDragging        = false;
        _tabStripRightCache      = 0;
        _dragTabWidths           = [];
        _lastFolderDragLocalX    = double.NaN;
        _folderIndicator         = null;
        _folderIndicatorChildIdx = -1;

        bool first = true;
        foreach (var filePath in files)
        {
            if (!Directory.Exists(filePath)) continue;
            string url = "file:///" + filePath.Replace('\\', '/');

            // 重複チェックなし: 同じフォルダでも新タブとして追加する

            _tabs.Insert(Math.Min(insertAt, _tabs.Count), new TabData(url));
            if (first)
            {
                _activeIndex = insertAt;
                Navigate(filePath);
                first = false;
            }
            insertAt++;
        }
        RebuildTabStrip();
        e.Handled = true;
    }

    private void OnBarDragLeave(object sender, DragEventArgs e)
    {
        if (!_isFolderDragging) return;

        // WPFのDragLeaveは子要素の境界をまたぐだけでも誤発火する。
        // Win32のGetCursorPosでカーソルが実際にこのウィンドウのHWND外に
        // 出ているかを確認してからリセットする。
        GetCursorPos(out POINT curPt);
        var barHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr hwndUnder = WindowFromPoint(curPt);

        // カーソル下のウィンドウがこのバーまたはその子であればDragLeaveを無視
        var cur = hwndUnder;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if (cur == barHwnd) return; // まだバー内 → 無視
            cur = GetParent(cur);
        }

        // 実際にバー外へ出た
        RemoveFolderIndicator();
        _dropTargetIndex         = -1;
        _isFolderDragging        = false;
        _tabStripRightCache      = 0;
        _dragTabWidths           = [];
        _lastFolderDragLocalX    = double.NaN;
        _folderIndicator         = null;
        _folderIndicatorChildIdx = -1;
        RebuildTabStrip();
    }

    // フォルダD&D中の前回localX（ノイズ除去用）
    private double _lastFolderDragLocalX = double.NaN;
    // フォルダD&D中インジケーター縦線（RebuildTabStripを呼ばず直接操作）
    private Border? _folderIndicator;
    // 前回のインジケーター挿入Childrenインデックス（-1=なし）
    private int _folderIndicatorChildIdx = -1;

    private void OnBarDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        // ドラッグ開始時のみスナップショット取得
        if (!_isFolderDragging)
        {
            _isFolderDragging        = true;
            _lastFolderDragLocalX    = double.NaN;
            _folderIndicator         = null;
            _folderIndicatorChildIdx = -1;

            // タブ幅スナップショット（D&D開始前の静止状態を記録）
            var widths = new System.Collections.Generic.List<double>();
            double total = 0;
            foreach (var child in _tabStrip.Children)
            {
                if (child is not FrameworkElement fe || !fe.IsHitTestVisible) continue;
                double w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width;
                widths.Add(w);
                total += w;
            }
            _dragTabWidths      = widths.ToArray();
            _tabStripRightCache = total;
            _dropTargetIndex    = -1;
        }

        // Win32 GetCursorPosで座標取得（e.GetPosition(_tabStrip)は子要素上で不正確になる）
        GetCursorPos(out POINT curPt);
        var stripOrigin = _tabStrip.PointToScreen(new Point(0, 0));
        double localX = curPt.X - stripOrigin.X;
        if (!double.IsNaN(_lastFolderDragLocalX) && Math.Abs(localX - _lastFolderDragLocalX) < 8.0)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        _lastFolderDragLocalX = localX;

        int newIndex = CalcFolderDropIndex(localX);

        if (newIndex != _dropTargetIndex)
        {
            _dropTargetIndex = newIndex;
            // RebuildTabStripは呼ばない。インジケーターBorderのみ差し替え。
            UpdateFolderIndicator(newIndex);
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    /// フォルダD&D中、インジケーター縦線のみを_tabStrip.Childrenに直接操作する。
    /// RebuildTabStripを呼ばないため、既存のタブチップはそのまま。
    /// </summary>
    private void UpdateFolderIndicator(int insertIndex)
    {
        // 既存インジケーターを除去
        RemoveFolderIndicator();

        if (insertIndex == CloneZoneIndex || insertIndex < 0) return;

        // 挿入先のChildrenインデックスを計算
        // _tabStrip.Children はタブチップ＋＋ボタンで構成される。
        // タブチップをカウントしてinsertIndex番目の直前に挿入。
        int tabChipCount = 0;
        int childInsertIdx = _tabStrip.Children.Count; // デフォルト末尾（＋ボタンの前）

        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (tabChipCount == insertIndex)
            {
                childInsertIdx = i;
                break;
            }
            var child = _tabStrip.Children[i];
            // IsHitTestVisible=false のインジケーターはスキップ
            if (child is FrameworkElement fe && fe.IsHitTestVisible)
                tabChipCount++;
        }

        _folderIndicator = MakeDropIndicator();
        int idx = Math.Min(childInsertIdx, _tabStrip.Children.Count);
        _tabStrip.Children.Insert(idx, _folderIndicator);
        _folderIndicatorChildIdx = idx;
    }

    private void RemoveFolderIndicator()
    {
        if (_folderIndicator == null) return;
        if (_tabStrip.Children.Contains(_folderIndicator))
            _tabStrip.Children.Remove(_folderIndicator);
        _folderIndicator         = null;
        _folderIndicatorChildIdx = -1;
    }

    private int CalcFolderDropIndex(double localX)
    {
        // タブ列より右側はCloneZone
        if (localX > _tabStripRightCache + 40)
            return CloneZoneIndex;

        int newIndex = CalcInsertFromSnapshot(localX);

        // ヒステリシス: 前回と異なるインデックスになったとき、
        // 前回インデックスが示す境界X（前回insと前回ins-1 の境）の周囲±dead以内なら維持。
        // dead = 現在近傍タブ幅の1/4（最小16px）
        if (newIndex != _dropTargetIndex && _dropTargetIndex >= 0 &&
            _dropTargetIndex != CloneZoneIndex && _dragTabWidths.Length > 0)
        {
            // 前回インデックスの境界X を求める
            double prevBoundary = 0;
            int prevIdx = Math.Min(_dropTargetIndex, _dragTabWidths.Length);
            for (int i = 0; i < prevIdx; i++)
                prevBoundary += _dragTabWidths[i];

            // dead zone = 隣接タブ幅の1/4（最小16px）
            double neighborW = prevIdx < _dragTabWidths.Length
                ? _dragTabWidths[prevIdx]
                : (prevIdx > 0 ? _dragTabWidths[prevIdx - 1] : 80.0);
            double dead = Math.Max(16.0, neighborW / 4.0);

            if (Math.Abs(localX - prevBoundary) < dead)
                newIndex = _dropTargetIndex;
        }
        return newIndex;
    }

    private int CalcInsertFromSnapshot(double localX)
    {
        if (localX <= 0) return 0; // バー左端より左はインデックス0
        double accum = 0;
        for (int i = 0; i < _dragTabWidths.Length; i++)
        {
            double w = _dragTabWidths[i];
            if (localX < accum + w / 2) return i;
            if (localX < accum + w)     return i + 1;
            accum += w;
        }
        return _dragTabWidths.Length;
    }

    // ── テーマ ────────────────────────────────────────────────────
    private void ApplyThemeToRoot()
    {
        _rootBorder.Background  = new SolidColorBrush(BgColor);
        _rootBorder.BorderBrush = new SolidColorBrush(BorderColor);
    }

    public void RefreshTheme()
    {
        ApplyThemeToRoot();
        RebuildTabStrip();
    }

    // ── 子ウィンドウ化 ────────────────────────────────────────────
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var barHwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(barHwnd, GWL_EXSTYLE);
        SetWindowLong(barHwnd, GWL_EXSTYLE,
            exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetParent(barHwnd, _explorer.MainHwnd);
        _parentSet = true;
        // BarRegistryに登録（Explorer間ドラッグドロップ用）
        BarRegistry.Register(barHwnd, this);
        ApplyPosition();
    }

    // ── 位置制御 ─────────────────────────────────────────────────
    public void PositionToExplorer()
    {
        if (!_parentSet) return;
        try { ApplyPosition(); } catch { }
    }

    private void ApplyPosition()
    {
        // タブバー非表示設定時は、SetWindowPos(SWP_SHOWWINDOW)による意図しない
        // 再表示を防ぐためここでガードする（PositionToExplorer経由の呼び出し全てに効く）。
        if (!_settings.ShowTabBar) return;

        var barHwnd = new WindowInteropHelper(this).Handle;
        if (barHwnd == IntPtr.Zero) return;
        if (!_explorer.TryGetBarInsertionPoint(out var mainRect, out int insertY)) return;

        var pt = new POINT { X = mainRect.Left, Y = insertY };
        ScreenToClient(_explorer.MainHwnd, ref pt);
        int barH = _settings.BarHeight;

        SetWindowPos(barHwnd, HWND_TOP, pt.X, pt.Y, mainRect.Width, barH,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // フラグなしで毎回PushShellViewDownを呼ぶ。
        // PushShellViewDown内部でガード（±3px）しているので
        // 既に正しい位置なら何もしない→ループしない。
        // フォーカス切り替えや最小化復元後も確実に押し込まれる。
        _explorer.PushShellViewDown(insertY, barH);
    }

    public void ShowBar()
    {
        // 設定でタブバーがOFFにされている場合は表示せず、非表示処理に倒す
        if (!_settings.ShowTabBar) { HideBar(); return; }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        PositionToExplorer();
    }

    public void HideBar()
    {
        Visibility = Visibility.Hidden;
        try
        {
            if (_explorer.TryGetBarInsertionPoint(out _, out int insertY))
                _explorer.RestoreShellView(insertY);
        }
        catch { }
    }

    /// <summary>
    /// 「タブバーを表示する」設定（AppSettings.ShowTabBar）とExplorerウィンドウの
    /// 現在の表示状態（最小化等）の両方から、このバーの表示/非表示を再評価する。
    /// 設定画面でのチェック切り替え時（App.ApplyTabBarVisibility経由）や、
    /// バー新規作成時（App.EnsureBar）から呼ばれる。
    /// </summary>
    public void RefreshTabBarVisibility()
    {
        if (_settings.ShowTabBar && _explorer.IsVisible)
        {
            ShowBar();
            RepushShellView();
        }
        else
        {
            HideBar();
        }
    }

    /// <summary>
    /// ShellViewが勝手に動いたとき（非アクティブ時のレイアウトリセット等）に
    /// 即時再押し込みする。WinEventHookから呼ばれるのでタイマー不要。
    /// </summary>
    public void RepushShellView()
    {
        if (!_parentSet || Visibility != Visibility.Visible) return;
        try
        {
            if (_explorer.TryGetBarInsertionPoint(out _, out int insertY))
                _explorer.PushShellViewDown(insertY, _settings.BarHeight);
        }
        catch { }
    }

    // ── タイマー ─────────────────────────────────────────────────
    private void OnTimer(object? sender, EventArgs e)
    {
        if (_timerRunning) return;
        _timerRunning = true;
        try { SyncTabsFromShell(); }
        catch { }
        finally { _timerRunning = false; }
    }

    private void FirstSync() => SyncTabsFromShell();

    private void SyncTabsFromShell()
    {
        var allTabs = ShellHelper.GetAllExplorerTabs();
        var mine    = allTabs.Where(t => t.Hwnd == _explorer.MainHwnd).ToList();
        if (mine.Count == 0) return;

        string currentUrl = mine[0].LocationURL;
        if (string.IsNullOrEmpty(currentUrl)) return;

        if (_tabs.Count == 0)
        {
            _tabs.Add(new TabData(currentUrl));
            _activeIndex = 0;
            RebuildTabStrip();
            return;
        }

        if (_activeIndex < _tabs.Count && _tabs[_activeIndex].Url != currentUrl)
        {
            _tabs[_activeIndex].Url = currentUrl;
            SettingsStore.AddHistory(_settings, currentUrl);
            _saveSettings();
            RebuildTabStrip();
        }
    }

    // ── フォルダアイコン取得 ──────────────────────────────────────
    private static ImageSource? GetFolderIcon(string localPath)
    {
        try
        {
            if (!Directory.Exists(localPath)) return null;
            var shfi = new SHFILEINFO();
            IntPtr hr = SHGetFileInfo(localPath, FILE_ATTRIBUTE_DIRECTORY, ref shfi,
                (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_SMALLICON);
            if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            DestroyIcon(shfi.hIcon);
            return src;
        }
        catch { return null; }
    }

    // ── タブUI ───────────────────────────────────────────────────
    public void RebuildTabStrip()
    {
        _tabStrip.Children.Clear();
        // タブ列右端をリセット（どちらのドラッグ中でもないときは再計算後に更新）
        bool anyDragging = _isDragging || _isFolderDragging || _isCrossBarTarget;
        if (!anyDragging) _tabStripRightCache = 0;
        if (_tabs.Count == 0)
        {
            _tabStrip.Children.Add(new TextBlock
            {
                Text = "タブなし", FontSize = 11,
                Foreground = new SolidColorBrush(TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
            return;
        }
        for (int i = 0; i < _tabs.Count; i++)
        {
            // インジケーター縦線: タブ内ドラッグ(_isDragging)のみ。
            // フォルダD&D(_isFolderDragging)はUpdateFolderIndicatorが担当。
            if (_isDragging && _dropTargetIndex == i && _dropTargetIndex != CloneZoneIndex)
                _tabStrip.Children.Add(MakeDropIndicator());
            // クロスBarドラッグ受け入れインジケーター
            if (_isCrossBarTarget && _crossBarDropIndex == i)
                _tabStrip.Children.Add(MakeCrossBarIndicator());
            _tabStrip.Children.Add(MakeTabChip(i));
        }
        // 末尾インジケーター（タブ内ドラッグのみ）
        if (_isDragging && _dropTargetIndex == _tabs.Count && _dropTargetIndex != CloneZoneIndex)
            _tabStrip.Children.Add(MakeDropIndicator());
        // クロスBarの末尾インジケーター
        if (_isCrossBarTarget && _crossBarDropIndex == _tabs.Count)
            _tabStrip.Children.Add(MakeCrossBarIndicator());

        // ClonePreview: タブ内ドラッグのみ（フォルダD&DはCloneZoneを使わない）
        if (_isDragging && _dropTargetIndex == CloneZoneIndex)
        {
            string previewLabel = _dragSourceIndex >= 0 && _dragSourceIndex < _tabs.Count
                ? _tabs[_dragSourceIndex].DisplayName : "";
            if (previewLabel.Length > 0)
                _tabStrip.Children.Add(MakeClonePreview(previewLabel));
        }

        // ＋ボタン: タブ末尾の直後（インジケーター・ClonePreviewの後）に配置
        // ドラッグ中も表示（ゴーストが視覚的役割を担うため）
        _tabStrip.Children.Add(MakePlusButton());
    }

    private UIElement MakePlusButton()
    {
        int h = Math.Max(_settings.BarHeight - 6, 20);
        var inner = new TextBlock
        {
            Text = "＋", FontSize = 13,
            Foreground = new SolidColorBrush(TextMuted),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var btn = new Border
        {
            Width        = h, Height = h,
            Margin       = new Thickness(4, 2, 2, 2),
            CornerRadius = new CornerRadius(4),
            Background   = new SolidColorBrush(IsDark
                ? Color.FromRgb(48, 48, 48) : Color.FromRgb(228, 228, 228)),
            Cursor  = Cursors.Hand,
            ToolTip = _settings.NewTabAction == "duplicate"
                ? "新しいタブ（現在を複製）" : "新しいタブ（ホームフォルダ）",
            Child   = inner,
        };
        btn.MouseEnter += (_, _) =>
        {
            btn.Background = new SolidColorBrush(IsDark
                ? Color.FromRgb(68, 68, 68) : Color.FromRgb(200, 200, 200));
            inner.Foreground = new SolidColorBrush(TextActive);
        };
        btn.MouseLeave += (_, _) =>
        {
            btn.Background = new SolidColorBrush(IsDark
                ? Color.FromRgb(48, 48, 48) : Color.FromRgb(228, 228, 228));
            inner.Foreground = new SolidColorBrush(TextMuted);
        };
        btn.MouseLeftButtonDown += (_, _) =>
        {
            if (_settings.NewTabAction == "duplicate") DuplicateCurrentTab();
            else AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        };
        return btn;
    }

    private UIElement MakeClonePreview(string label)
    {
        Color accent = Accent;
        if (_activeGroup != null) accent = GroupEditDialog.HexToColor(_activeGroup.Color);

        var chip = new Border
        {
            Height          = Math.Max(_settings.BarHeight - 4, 22),
            MinWidth        = 80,
            MaxWidth        = 180,
            Margin          = new Thickness(6, 2, 0, 2),
            Padding         = new Thickness(6, 0, 4, 0),
            CornerRadius    = new CornerRadius(4, 4, 0, 0),
            Background      = new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(160, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Opacity         = 0.6,
            IsHitTestVisible = false,
        };
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(new TextBlock
        {
            Text = "📁 ", FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(accent),
        });
        inner.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(accent),
        });
        chip.Child = inner;
        return chip;
    }

    private Border MakeDropIndicator()
    {
        return new Border
        {
            Width           = 3,
            Margin          = new Thickness(0, 4, 2, 4),
            Background      = new SolidColorBrush(Accent),
            CornerRadius    = new CornerRadius(1),
            IsHitTestVisible = false,
        };
    }

    /// <summary>クロスBarドラッグ受け入れ時の縦線インジケーター</summary>
    private Border MakeCrossBarIndicator()
    {
        return new Border
        {
            Width           = 3,
            Margin          = new Thickness(0, 4, 2, 4),
            Background      = new SolidColorBrush(Accent),
            CornerRadius    = new CornerRadius(1),
            IsHitTestVisible = false,
        };
    }

    private UIElement MakeTabChip(int index)
    {
        var tab    = _tabs[index];
        bool active = index == _activeIndex;
        bool dragging = _isDragging && _dragSourceIndex == index;
        int  ci    = index;

        // ドロップインジケーター: _dropTargetIndex==index のとき左端に縦線
        bool showIndicator = _isDragging && _dropTargetIndex == index;
        var chip = new Border
        {
            Height          = Math.Max(_settings.BarHeight - 4, 22),
            MinWidth        = 90,
            MaxWidth        = 200,
            Margin          = new Thickness(2, 2, 0, 2),
            Padding         = new Thickness(6, 0, 4, 0),
            CornerRadius    = new CornerRadius(4, 4, 0, 0),
            Cursor          = Cursors.Hand,
            ToolTip         = tab.LocalPath,
            Opacity         = 1.0,
        };

        // アクセントカラー: グループ色があればそれを、なければWindowsブルー
        Color accentColor = Accent;
        if (_activeGroup != null)
            accentColor = GroupEditDialog.HexToColor(_activeGroup.Color);

        if (active && !dragging)
        {
            chip.Background      = new SolidColorBrush(TabActiveBg);
            chip.BorderBrush     = new SolidColorBrush(accentColor);
            chip.BorderThickness = new Thickness(0, 2, 0, 0);
        }
        else if (dragging)
        {
            // ドラッグ元タブ: 薄いアウトラインだけ（ゴーストが視覚的役割を担う）
            chip.Background      = new SolidColorBrush(Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B));
            chip.BorderBrush     = new SolidColorBrush(Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B));
            chip.BorderThickness = new Thickness(1);
        }
        else
        {
            chip.Background      = new SolidColorBrush(TabInactiveBg);
            chip.BorderThickness = new Thickness(0);
        }

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // フォルダアイコン（クリックでサブフォルダメニューを開く）
        UIElement innerIcon;
        var iconImg = GetFolderIcon(tab.LocalPath);
        if (iconImg != null)
        {
            innerIcon = new System.Windows.Controls.Image
            {
                Source = iconImg, Width = 14, Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            innerIcon = new TextBlock
            {
                Text = tab.Locked ? "🔒" : "📁",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // アイコンを Border でラップしてクリック領域を確保
        var iconEl = new Border
        {
            Margin            = new Thickness(0, 0, 4, 0),
            Padding           = new Thickness(1),
            Background        = Brushes.Transparent,
            Cursor            = (_settings.SubFolderMenu.Enabled &&
                                 _settings.SubFolderMenu.EnableTabIconSubFolder)
                                ? Cursors.Hand : Cursors.Arrow,
            Child             = innerIcon,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 左クリック: サブフォルダメニュー展開
        iconEl.MouseLeftButtonDown += (_, e) =>
        {
            if (!_settings.SubFolderMenu.Enabled ||
                !_settings.SubFolderMenu.EnableTabIconSubFolder) return;
            var pt = iconEl.PointToScreen(new Point(0, iconEl.ActualHeight));
            _subFolderMenuService?.ShowForTabIcon(
                _tabs[ci].LocalPath, pt.X, pt.Y, _explorer.MainHwnd);
            e.Handled = true; // チップクリックのドラッグ起動を防ぐ
        };

        // 右クリック: 上位階層のメニュー
        iconEl.MouseRightButtonDown += (_, e) =>
        {
            if (!_settings.SubFolderMenu.Enabled ||
                !_settings.SubFolderMenu.EnableTabIconSubFolder) return;
            var pt = iconEl.PointToScreen(new Point(0, iconEl.ActualHeight));
            _subFolderMenuService?.ShowParentForTabIcon(
                _tabs[ci].LocalPath, pt.X, pt.Y, _explorer.MainHwnd);
            e.Handled = true;
        };

        Grid.SetColumn(iconEl, 0);

        var lbl = new TextBlock
        {
            Text              = tab.DisplayName,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = new SolidColorBrush(active ? TextActive : TextInactive),
            FontWeight        = active ? FontWeights.SemiBold : FontWeights.Normal,
        };
        Grid.SetColumn(lbl, 1);

        // ×ボタン: Borderで囲んでクリック領域を広く
        var closeBtnInner = new TextBlock
        {
            Text = "×", FontSize = 14,
            Foreground = new SolidColorBrush(TextMuted),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var closeBtn = new Border
        {
            Width   = 22, Height = 22,
            Margin  = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            Background   = MediaBrushes.Transparent,
            Cursor       = Cursors.Arrow,
            Child        = closeBtnInner,
            Visibility   = tab.Locked ? Visibility.Hidden : Visibility.Visible,
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.MouseEnter += (_, _) =>
        {
            closeBtnInner.Foreground = MediaBrushes.OrangeRed;
            closeBtnInner.FontWeight = FontWeights.Bold;
        };
        closeBtn.MouseLeave += (_, _) =>
        {
            closeBtnInner.Foreground = new SolidColorBrush(TextMuted);
            closeBtnInner.FontWeight = FontWeights.Normal;
        };
        closeBtn.MouseLeftButtonDown += (_, e) => { CloseTab(ci); e.Handled = true; };
        Grid.SetColumn(closeBtn, 2);

        row.Children.Add(iconEl);
        row.Children.Add(lbl);
        row.Children.Add(closeBtn);
        chip.Child = row;

        // マウスイベント
        chip.MouseLeftButtonDown += (_, e) =>
        {
            _dragStartPos    = e.GetPosition(this);
            _dragSourceIndex = ci;
            chip.CaptureMouse();
            e.Handled = true;
        };
        chip.MouseMove        += (_, e) => OnChipMouseMove(e, chip, ci);
        chip.MouseLeftButtonUp += (_, e) =>
        {
            chip.ReleaseMouseCapture();
            bool wasDragging = _isDragging;
            // TabDragServiceのタイマーがDropを検知してOnTabDroppedを呼ぶ
            if (!wasDragging)
            {
                _isDragging      = false;
                _dragSourceIndex = -1;
                _dropTargetIndex = -1;
                RebuildTabStrip();
                SwitchTab(ci);
            }
            e.Handled = true;
        };
        chip.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && !tab.Locked) CloseTab(ci);
        };
        chip.MouseRightButtonDown += (_, e) =>
        {
            ShowTabContextMenu(ci, chip); e.Handled = true;
        };
        chip.MouseEnter += (_, _) =>
        {
            if (ci != _activeIndex && !(_isDragging && _dragSourceIndex == ci))
                chip.Background = new SolidColorBrush(TabHoverBg);
        };
        chip.MouseLeave += (_, _) =>
        {
            if (ci != _activeIndex && !(_isDragging && _dragSourceIndex == ci))
                chip.Background = new SolidColorBrush(TabInactiveBg);
        };

        return chip;
    }

    // ── ドラッグ ─────────────────────────────────────────────────
    private void OnChipMouseMove(MouseEventArgs e, Border chip, int index)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragSourceIndex != index) return;

        Point pos  = e.GetPosition(this);
        double dist = (pos - _dragStartPos).Length;

        if (!_isDragging && dist > 8)
        {
            _isDragging = true;
            // TabDragServiceでゴーストを開始（Explorer外まで追従できる独立ウィンドウ）
            var tab = _tabs[_dragSourceIndex];
            Color accent = Accent;
            if (_activeGroup != null)
                accent = GroupEditDialog.HexToColor(_activeGroup.Color);
            // タブチップのスクリーン座標を取得してゴーストの初期位置にする
            GetCursorPos(out POINT startPt);
            // chip要素をPointToScreen変換してタブ左上のスクリーン座標を取得
            var chipScreenPt = chip.PointToScreen(new Point(0, 0));
            double chipW = chip.ActualWidth > 0  ? chip.ActualWidth  : 140;
            double chipH = chip.ActualHeight > 0 ? chip.ActualHeight : Math.Max(_settings.BarHeight - 4, 22);
            TabDragService.Instance.StartDrag(this, tab.Url, tab.DisplayName, accent,
                chipScreenPt.X, chipScreenPt.Y,
                chipW, chipH, IsDark);
            // ドロップイベントを購読（1回だけ）
            TabDragService.Instance.TabDropped  -= OnTabDropped;
            TabDragService.Instance.DragCancelled -= OnDragCancelled;
            TabDragService.Instance.TabDropped  += OnTabDropped;
            TabDragService.Instance.DragCancelled += OnDragCancelled;
        }
        if (!_isDragging) return;

        // インジケーター更新はUpdateDragIndicator()でタイマーから呼ばれる
        // 縦方向分離はTabDragServiceのタイマーが自動検知するため不要
    }

    // ── ドラッグ完了コールバック ─────────────────────────────────
    private void OnTabDropped(OverlayBar target, string rawUrl)
    {
        TabDragService.Instance.TabDropped   -= OnTabDropped;
        TabDragService.Instance.DragCancelled -= OnDragCancelled;

        _isDragging      = false;
        _dragSourceIndex = -1;

        // マーカーでドロップ種別を識別
        bool isDetach = rawUrl.StartsWith("__DETACH__:", StringComparison.Ordinal);
        bool isSame   = rawUrl.StartsWith("__SAME__:",   StringComparison.Ordinal);
        string url    = isDetach ? rawUrl["__DETACH__:".Length..]
                      : isSame   ? rawUrl["__SAME__:".Length..]
                      : rawUrl;
        string local  = ShellHelper.UrlToLocalPath(url);

        if (isSame && target == this)
        {
            // 同じBar内ドロップ確定
            int srcIdx = _tabs.FindIndex(t => t.Url == url);

            // _dropTargetIndex == _tabs.Count かつ バー末尾の右端 → 複製
            // _dropTargetIndex == -1 → インジケーターなし（空白）→ 複製
            // それ以外 → 並べ替え
            // CloneZoneIndex = 複製ゾーンへのドロップ
            bool isClone = (_dropTargetIndex < 0 || _dropTargetIndex == CloneZoneIndex);

            if (srcIdx >= 0 && !isClone && _dropTargetIndex >= 0)
            {
                // ── 並べ替え確定（左右何個でも） ─────────────────────────
                int insertAt = _dropTargetIndex;
                var moved = _tabs[srcIdx];
                _tabs.RemoveAt(srcIdx);
                if (insertAt > srcIdx) insertAt--;
                insertAt = Math.Max(0, Math.Min(insertAt, _tabs.Count));
                _tabs.Insert(insertAt, moved);

                // 移動したタブをアクティブにする
                _activeIndex = insertAt;
                Navigate(_tabs[_activeIndex].LocalPath);
            }
            else if (srcIdx >= 0 && isClone)
            {
                // ── 複製（CloneZone＝バー末尾空白へドロップ） ─────────────
                // 元タブの右ではなく末尾に追加
                _tabs.Add(new TabData(url));
                _activeIndex = _tabs.Count - 1;
                Navigate(_tabs[_activeIndex].LocalPath);
            }

            _dropTargetIndex = -1;
            RebuildTabStrip();
            return;
        }

        int idx = _tabs.FindIndex(t => t.Url == url);
        if (idx < 0) { RebuildTabStrip(); return; }

        if (!isDetach && target != this)
        {
            // ── 別のExplorerのBarへタブ移動 ──────────────────────
            _tabs.RemoveAt(idx);
            if (_tabs.Count == 0)
            {
                // 最後のタブ → 自分のExplorerを閉じる
                PostMessage(_explorer.MainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);
                Navigate(_tabs[_activeIndex].LocalPath);
                RebuildTabStrip();
            }
            // クロスBarドロップ: インジケーターが示す位置に挿入
            int crossIdx = target.GetCrossBarDropIndex();
            target.AddTabAt(url, crossIdx, switchTo: true);
        }
        else
        {
            // ── Bar外ドロップ → 新しいExplorerウィンドウとして開く ─
            bool isLastTab = _tabs.Count == 1;
            if (!isLastTab)
            {
                _tabs.RemoveAt(idx);
                _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);
                Navigate(_tabs[_activeIndex].LocalPath);
                RebuildTabStrip();
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + local + "\""); } catch { }
            }
            // タブ1枚のBar外ドロップは何もしない（消えてしまうので）
        }
    }

    private void OnDragCancelled(OverlayBar source)
    {
        TabDragService.Instance.TabDropped   -= OnTabDropped;
        TabDragService.Instance.DragCancelled -= OnDragCancelled;
        _isDragging       = false;
        _dragSourceIndex  = -1;
        _dropTargetIndex  = -1;
        _dragTabWidths    = [];
        _tabStripRightCache = 0;
        // クロスBarインジケーターもクリア（念のため）
        _isCrossBarTarget   = false;
        _crossBarDropIndex  = -1;
        _crossBarTabWidths  = [];
        _crossBarStripRight = 0;
        RebuildTabStrip();
    }

    /// <summary>
    /// ドラッグ中にタイマーから呼ばれ、カーソル位置からインジケーター位置を更新する。
    /// Win32のGetCursorPosでスクリーン座標を取得し、
    /// _tabStripのスクリーン位置と比較してローカルXを計算する。
    /// </summary>
    // _dropTargetIndex の特殊値:
    //   -1             = バー外・インジケーターなし
    //   0.._tabs.Count = 挿入インデックス（並べ替え）
    //   _tabs.Count+1  = 末尾タブより右の空白（複製ゾーン）
    private const int CloneZoneIndex = int.MaxValue;

    // タブ幅スナップショット（ドラッグ開始時に固定）
    private double[] _dragTabWidths = [];

    public void UpdateDragIndicator()
    {
        if (!_isDragging) return;
        if (!TabDragService.Instance.IsDragging) return;
        if (TabDragService.Instance.DragSource != this) return;

        try
        {
            GetCursorPos(out POINT curPt);
            var barOrigin   = this.PointToScreen(new Point(0, 0));
            var stripOrigin = _tabStrip.PointToScreen(new Point(0, 0));

            // バーの範囲外 → インジケーターなし
            if (curPt.X < barOrigin.X || curPt.X > barOrigin.X + this.ActualWidth ||
                curPt.Y < barOrigin.Y || curPt.Y > barOrigin.Y + this.ActualHeight)
            {
                if (_dropTargetIndex != -1)
                {
                    _dropTargetIndex = -1;
                    _dragTabWidths   = [];
                    RebuildTabStrip();
                }
                return;
            }

            // スナップショット: ドラッグ開始時のみ取得（RebuildTabStripで変化しない）
            if (_dragTabWidths.Length == 0)
            {
                var widths = new System.Collections.Generic.List<double>();
                double total = 0;
                foreach (var child in _tabStrip.Children)
                {
                    if (child is not FrameworkElement fe || !fe.IsHitTestVisible) continue;
                    widths.Add(fe.ActualWidth);
                    total += fe.ActualWidth;
                }
                _dragTabWidths      = widths.ToArray();
                _tabStripRightCache = total;
            }

            double localX        = curPt.X - stripOrigin.X;
            double tabStripRight = _tabStripRightCache;

            // 挿入インデックスをスナップショットで計算
            int newIndex;
            if (_dropTargetIndex == CloneZoneIndex)
                newIndex = localX > tabStripRight + 10 ? CloneZoneIndex : CalcInsertIndex(localX);
            else
                newIndex = localX > tabStripRight + 40 ? CloneZoneIndex : CalcInsertIndex(localX);

            // ヒステリシス（±16px）
            if (newIndex >= 0 && newIndex != CloneZoneIndex && newIndex != _dropTargetIndex
                && _dropTargetIndex >= 0)
            {
                double bx = BoundaryX(newIndex);
                if (Math.Abs(localX - bx) <= 16) return;
            }

            if (newIndex == _dropTargetIndex) return;

            bool cloneChanged = (newIndex == CloneZoneIndex) != (_dropTargetIndex == CloneZoneIndex);
            _dropTargetIndex = newIndex;
            // CloneZone切り替え時のみRebuildTabStrip（ClonePreview追加/削除のため）
            // それ以外はRebuildTabStripを呼ばずにMakeTabChip内のshowIndicatorで描画済み
            if (cloneChanged)
            {
                _dragTabWidths = []; // CloneZone切替時はスナップショットリセット
                RebuildTabStrip();
            }
            else
            {
                // インジケーター位置だけ更新: タブチップのmarginを変更するより
                // RebuildTabStripを呼ぶほうがシンプルだが、
                // _tabStripRightCacheを保持したままRebuildすることでちらつきを防ぐ
                double savedCache = _tabStripRightCache;
                double[] savedWidths = _dragTabWidths;
                RebuildTabStrip();
                _tabStripRightCache = savedCache;
                _dragTabWidths      = savedWidths;
            }
        }
        catch { }
    }

    // ── クロスBarドラッグ受け入れ ──────────────────────────────────
    /// <summary>
    /// 他のBarからドラッグ中にカーソルがこのBarに入った/移動した際に呼ばれる。
    /// カーソル位置から挿入インデックスを計算し、インジケーターを表示する。
    /// TabDragService.OnTrackerTickから毎フレーム呼ばれる。
    /// </summary>
    public void UpdateCrossBarIndicator()
    {
        if (!TabDragService.Instance.IsDragging) return;
        if (TabDragService.Instance.DragSource == this) return;

        try
        {
            GetCursorPos(out POINT curPt);
            var barOrigin   = this.PointToScreen(new Point(0, 0));
            var stripOrigin = _tabStrip.PointToScreen(new Point(0, 0));

            // バーの範囲外チェック
            if (curPt.X < barOrigin.X || curPt.X > barOrigin.X + this.ActualWidth ||
                curPt.Y < barOrigin.Y || curPt.Y > barOrigin.Y + this.ActualHeight)
            {
                ClearCrossBarIndicator();
                return;
            }

            // タブ幅スナップショット（初回のみ取得）
            if (!_isCrossBarTarget || _crossBarTabWidths.Length == 0)
            {
                _isCrossBarTarget = true;
                var widths = new System.Collections.Generic.List<double>();
                double total = 0;
                foreach (var child in _tabStrip.Children)
                {
                    if (child is not FrameworkElement fe || !fe.IsHitTestVisible) continue;
                    widths.Add(fe.ActualWidth);
                    total += fe.ActualWidth;
                }
                _crossBarTabWidths  = widths.ToArray();
                _crossBarStripRight = total;
            }

            double localX   = curPt.X - stripOrigin.X;
            int    newIndex = CalcCrossBarInsertIndex(localX);

            if (newIndex == _crossBarDropIndex) return;

            double[] savedWidths = _crossBarTabWidths;
            double   savedRight  = _crossBarStripRight;
            _crossBarDropIndex = newIndex;
            RebuildTabStrip();
            // RebuildTabStrip後もスナップショットを維持
            _crossBarTabWidths  = savedWidths;
            _crossBarStripRight = savedRight;
        }
        catch { }
    }

    /// <summary>クロスBarインジケーターをクリアしてRebuildする。</summary>
    public void ClearCrossBarIndicator()
    {
        if (!_isCrossBarTarget) return;
        _isCrossBarTarget   = false;
        _crossBarDropIndex  = -1;
        _crossBarTabWidths  = [];
        _crossBarStripRight = 0;
        RebuildTabStrip();
    }

    /// <summary>クロスBarドロップ時の挿入インデックスを返す。</summary>
    public int GetCrossBarDropIndex() => _crossBarDropIndex;

    private int CalcCrossBarInsertIndex(double localX)
    {
        if (localX <= 0) return 0;
        double accum = 0;
        for (int i = 0; i < _crossBarTabWidths.Length; i++)
        {
            double w = _crossBarTabWidths[i];
            if (localX < accum + w / 2) return i;
            if (localX < accum + w)     return i + 1;
            accum += w;
        }
        return _crossBarTabWidths.Length;
    }

    private int CalcInsertIndex(double localX)
    {
        if (localX <= 0) return 0; // バー左端より左はインデックス0
        double accum = 0;
        for (int i = 0; i < _dragTabWidths.Length; i++)
        {
            double w = _dragTabWidths[i];
            if (localX < accum + w / 2) return i;
            if (localX < accum + w)     return i + 1;
            accum += w;
        }
        return _dragTabWidths.Length;
    }

    private double BoundaryX(int insertIndex)
    {
        double accum = 0;
        for (int i = 0; i < Math.Min(insertIndex, _dragTabWidths.Length); i++)
            accum += _dragTabWidths[i];
        return accum;
    }


    private int GetTabIndexAtX(double x)
    {
        double accum = 0;
        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is not FrameworkElement el) continue;
            double w = el.ActualWidth;
            if (x < accum + w / 2) return i;
            accum += w;
        }
        return Math.Max(0, _tabStrip.Children.Count - 1);
    }

    /// <summary>
    /// タブ間の「挿入位置」インデックスを返す（0〜_tabs.Count）。
    /// インジケーターのみ使用。ドラッグ中のインジケーター用Borderは除外。
    /// </summary>
    private int GetDropIndexAtX(double x)
    {
        double accum = 0;
        int tabIdx = 0;
        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is not FrameworkElement el) continue;
            // ドロップインジケーター（細い縦線）はスキップ
            if (el is Border b && b.Width <= 4 && !b.IsHitTestVisible) continue;
            double w = el.ActualWidth;
            if (x < accum + w / 2) return tabIdx;
            if (x < accum + w)     return tabIdx + 1;
            accum += w;
            tabIdx++;
        }
        return tabIdx;
    }

    /// <summary>挿入インデックスiの境界X座標を返す（ヒステリシス計算用）</summary>
    private double GetDropBoundaryX(int insertIndex)
    {
        double accum = 0;
        int tabIdx = 0;
        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            if (_tabStrip.Children[i] is not FrameworkElement el) continue;
            if (el is Border b && b.Width <= 4 && !b.IsHitTestVisible) continue;
            double w = el.ActualWidth;
            if (tabIdx == insertIndex)     return accum;          // このタブの左端
            if (tabIdx + 1 == insertIndex) return accum + w;      // このタブの右端
            accum += w;
            tabIdx++;
        }
        return accum; // 末尾
    }

    private bool IsDescendantOfBar(IntPtr hwnd)
    {
        var barHwnd = new WindowInteropHelper(this).Handle;
        var cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if (cur == barHwnd) return true;
            cur = GetParent(cur);
        }
        return false;
    }

    private void DetachTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || _tabs.Count <= 1) return;
        string path = _tabs[index].LocalPath;
        _tabs.RemoveAt(index);
        _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);
        _isDragging      = false;
        _dragSourceIndex = -1;
        Navigate(_tabs[_activeIndex].LocalPath);
        RebuildTabStrip();
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        _onTabDetached(path);
    }

    // ── タブ操作 ─────────────────────────────────────────────────
    private void SwitchTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _activeIndex = index;
        Navigate(_tabs[index].LocalPath);
        RebuildTabStrip();
    }

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        if (_tabs[index].Locked) return;
        if (_tabs.Count <= 1)
        {
            PostMessage(_explorer.MainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        _tabs.RemoveAt(index);
        _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);
        Navigate(_tabs[_activeIndex].LocalPath);
        RebuildTabStrip();
    }

    private void DuplicateCurrentTab()
    {
        string url = _tabs.Count > 0 ? _tabs[_activeIndex].Url
            : "file:///" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        _tabs.Add(new TabData(url));
        _activeIndex = _tabs.Count - 1;
        Navigate(_tabs[_activeIndex].LocalPath);
        RebuildTabStrip();
    }

    public void AddTab(string pathOrUrl, bool switchTo = true)
    {
        string url   = pathOrUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl : "file:///" + pathOrUrl.Replace('\\', '/');
        string local = ShellHelper.UrlToLocalPath(url);
        int exist = _tabs.FindIndex(t =>
            t.LocalPath.Equals(local, StringComparison.OrdinalIgnoreCase));
        if (exist >= 0) { SwitchTab(exist); return; }
        _tabs.Insert(_activeIndex + 1, new TabData(url));
        if (switchTo) { _activeIndex++; Navigate(local); }
        RebuildTabStrip();
    }

    /// <summary>
    /// 指定インデックスにタブを挿入する（クロスBarドロップ用）。
    /// insertAt が -1 または範囲外の場合はアクティブタブの次に挿入する。
    /// </summary>
    public void AddTabAt(string pathOrUrl, int insertAt, bool switchTo = true)
    {
        string url   = pathOrUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl : "file:///" + pathOrUrl.Replace('\\', '/');
        string local = ShellHelper.UrlToLocalPath(url);
        // クロスBarドロップ時は重複チェックをスキップして強制挿入する
        // （ドラッグ元で同名タブが削除されるため、ここで弾くとタブが消える）

        // クロスBarインジケーターをクリア（挿入前に状態リセット）
        _isCrossBarTarget   = false;
        _crossBarDropIndex  = -1;
        _crossBarTabWidths  = [];
        _crossBarStripRight = 0;

        // 有効なインデックスに補正
        int idx = (insertAt >= 0 && insertAt <= _tabs.Count) ? insertAt : _activeIndex + 1;
        _tabs.Insert(idx, new TabData(url));
        if (switchTo)
        {
            _activeIndex = idx;
            Navigate(local);
        }
        RebuildTabStrip();
    }

    // ── バー空白右クリック ────────────────────────────────────────
    private void ShowBarContextMenu()
    {
        var menu = new ContextMenu();
        void Add(string h, Action a)
        { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); menu.Items.Add(i); }

        Add("新しいタブ（現在を複製）", DuplicateCurrentTab);
        Add("タブスイッチャー",         ShowTabSwitcher);
        menu.Items.Add(new Separator());

        // グループ管理
        var groupMenu = new MenuItem { Header = "グループ" };

        // 現在のタブセットを保存
        var saveGrp = new MenuItem { Header = "現在のタブセットをグループとして保存..." };
        saveGrp.Click += (_, _) => SaveCurrentAsGroup();
        groupMenu.Items.Add(saveGrp);

        // 現在のアクティブタブをグループに追加
        if (_settings.TabGroups.Count > 0 && _tabs.Count > 0)
        {
            var addToGrp = new MenuItem { Header = "現在のタブをグループに追加..." };
            foreach (var g in _settings.TabGroups)
            {
                var grp = g;
                var clr = GroupEditDialog.HexToColor(grp.Color);
                var subItem = new MenuItem { Header = $"「{grp.Name}」に追加" };
                // 色スウォッチをアイコン代わりに
                subItem.Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 12,
                    Fill = new SolidColorBrush(clr),
                    RadiusX = 2, RadiusY = 2,
                };
                subItem.Click += (_, _) =>
                {
                    var currentTab = _tabs[_activeIndex];
                    // 重複チェック
                    if (!grp.Tabs.Any(t => t.Path.Equals(currentTab.Url, StringComparison.OrdinalIgnoreCase)
                                        || t.Path.Equals(currentTab.LocalPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        grp.Tabs.Add(new TabEntry { Path = currentTab.Url });
                        _saveSettings();
                    }
                };
                addToGrp.Items.Add(subItem);
            }
            groupMenu.Items.Add(addToGrp);
        }

        if (_settings.TabGroups.Count > 0)
        {
            groupMenu.Items.Add(new Separator());
            foreach (var g in _settings.TabGroups)
            {
                var grp = g;
                var clr = GroupEditDialog.HexToColor(grp.Color);
                var item = new MenuItem { Header = $"「{grp.Name}」を開く" };
                item.Icon = new System.Windows.Shapes.Rectangle
                {
                    Width = 12, Height = 12,
                    Fill = new SolidColorBrush(clr),
                    RadiusX = 2, RadiusY = 2,
                };
                item.Click += (_, _) => OpenGroup(grp);
                groupMenu.Items.Add(item);
            }
        }
        menu.Items.Add(groupMenu);

        menu.Items.Add(new Separator());
        Add("設定...", () => new SettingsWindow(_settings, _saveSettings, RefreshTheme).Show());
        menu.IsOpen = true;
    }

    // ── タブ右クリックメニュー ────────────────────────────────────
    private void ShowTabContextMenu(int index, UIElement target)
    {
        if (index < 0 || index >= _tabs.Count) return;
        var tab  = _tabs[index];
        var menu = new ContextMenu();
        void Add(string h, Action a, bool en = true)
        { var i = new MenuItem { Header = h, IsEnabled = en }; i.Click += (_, _) => a(); menu.Items.Add(i); }

        Add("複製して右に追加", () =>
        {
            _tabs.Insert(index + 1, new TabData(tab.Url));
            if (index < _activeIndex) _activeIndex++;
            RebuildTabStrip();
        });
        Add(tab.Locked ? "🔒 タブロック解除" : "🔒 タブをロック", () =>
        {
            _tabs[index].Locked = !_tabs[index].Locked;
            RebuildTabStrip();
        });
        menu.Items.Add(new Separator());
        Add("パスをコピー", () => { try { Clipboard.SetText(tab.LocalPath); } catch { } });
        Add("新しいウィンドウで開く", () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + tab.LocalPath + "\""); } catch { }
        });
        Add("このタブを分離して新ウィンドウに", () => DetachTab(index),
            _tabs.Count > 1 && !tab.Locked);
        menu.Items.Add(new Separator());
        Add("他のタブをすべて閉じる", () =>
        {
            var keep = _tabs[index];
            _tabs.Clear(); _tabs.Add(keep); _activeIndex = 0;
            Navigate(keep.LocalPath); RebuildTabStrip();
        });
        Add("右側のタブをすべて閉じる", () =>
        {
            while (_tabs.Count > index + 1) _tabs.RemoveAt(index + 1);
            _activeIndex = Math.Min(_activeIndex, _tabs.Count - 1);
            RebuildTabStrip();
        });
        menu.Items.Add(new Separator());
        Add("このタブを閉じる", () => CloseTab(index), !tab.Locked);
        menu.PlacementTarget = target;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    // ── タブスイッチャー ─────────────────────────────────────────
    private void ShowTabSwitcher()
    {
        var popup = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
        };
        var panel = new StackPanel
        {
            Background = new SolidColorBrush(IsDark
                ? Color.FromRgb(40, 40, 40) : Color.FromRgb(250, 250, 250)),
            MinWidth = 260,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "タブスイッチャー", FontSize = 10,
            Foreground = new SolidColorBrush(TextMuted),
            Margin = new Thickness(10, 6, 10, 4),
        });
        for (int i = 0; i < _tabs.Count; i++)
        {
            int ci = i; var tab = _tabs[i];
            var item = new Border
            {
                Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand,
                Background = i == _activeIndex
                    ? new SolidColorBrush(Accent) : MediaBrushes.Transparent,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = i == _activeIndex ? "▶ " : "    ", FontSize = 10,
                Foreground = i == _activeIndex ? MediaBrushes.White : new SolidColorBrush(TextMuted),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = tab.DisplayName, FontSize = 11,
                Foreground = i == _activeIndex ? MediaBrushes.White : new SolidColorBrush(TextActive),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200,
            });
            if (tab.Locked) row.Children.Add(new TextBlock { Text = " 🔒", FontSize = 9 });
            item.Child = row;
            item.MouseLeftButtonDown += (_, _) => { popup.IsOpen = false; SwitchTab(ci); };
            item.MouseEnter += (_, _) =>
            {
                if (ci != _activeIndex)
                    item.Background = new SolidColorBrush(IsDark
                        ? Color.FromRgb(60, 60, 60) : Color.FromRgb(230, 230, 230));
            };
            item.MouseLeave += (_, _) =>
            {
                if (ci != _activeIndex) item.Background = MediaBrushes.Transparent;
            };
            panel.Children.Add(item);
        }
        popup.Child = new Border
        {
            Child = panel, BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.3, Color = Colors.Black },
        };
        popup.IsOpen = true;
    }

    // ── グループ操作（右クリックメニューから呼ばれる）────────────
    private void OpenGroup(TabGroup group)
    {
        if (group.Tabs.Count == 0) return;
        _activeGroup = group;
        _tabs.Clear();
        foreach (var t in group.Tabs) _tabs.Add(new TabData(t.Path));
        _activeIndex = 0;
        Navigate(_tabs[0].LocalPath);
        RebuildTabStrip();
        group.LastUsed = DateTime.Now;
        _saveSettings();
    }

    private void SaveCurrentAsGroup()
    {
        var dlg = new GroupEditDialog(null);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        dlg.Result.Tabs = _tabs.Select(t => new TabEntry { Path = t.Url }).ToList();
        _settings.TabGroups.Add(dlg.Result);
        _saveSettings();
    }

    // ── 履歴 ─────────────────────────────────────────────────────
    private void ShowHistory()
    {
        var menu = new ContextMenu();
        foreach (var h in _settings.History.Take(40))
        {
            var entry = h;
            var item  = new MenuItem { Header = ShellHelper.GetDisplayName(entry.Path), ToolTip = entry.Path };
            item.Click += (_, _) => AddTab(entry.Path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "履歴はありません", IsEnabled = false });
        menu.IsOpen = true;
    }

    // ── メインメニュー ────────────────────────────────────────────
    private void ShowMenu()
    {
        var menu = new ContextMenu();
        void Add(string h, Action a)
        { var i = new MenuItem { Header = h }; i.Click += (_, _) => a(); menu.Items.Add(i); }

        Add("新しいタブ（現在を複製）", DuplicateCurrentTab);
        menu.Items.Add(new Separator());
        Add("現在のタブをグループとして保存...", SaveCurrentAsGroup);
        Add("ロックされていないタブをすべて閉じる", () =>
        {
            var locked = _tabs.Where(t => t.Locked).ToList();
            if (locked.Count == 0) { PostMessage(_explorer.MainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return; }
            _tabs.RemoveAll(t => !t.Locked);
            _activeIndex = 0;
            Navigate(_tabs[0].LocalPath); RebuildTabStrip();
        });
        menu.Items.Add(new Separator());
        Add("設定...", () => new SettingsWindow(_settings, _saveSettings, RefreshTheme).Show());
        menu.IsOpen = true;
    }

    // ── ナビゲーション ────────────────────────────────────────────
    private void Navigate(string localPath)
    {
        if (string.IsNullOrEmpty(localPath) || !Directory.Exists(localPath)) return;
        ShellHelper.NavigateTo(_explorer.MainHwnd, localPath);
        SettingsStore.AddHistory(_settings, localPath);
        _saveSettings();
    }

    // ── ユーティリティ ───────────────────────────────────────────
    private Border MakeIconBtn(string icon, string tip, Action onClick)
    {
        var btn = new Border
        {
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(3), Background = MediaBrushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = tip,
            Child = new TextBlock
            {
                Text = icon, FontSize = 12,
                Foreground = new SolidColorBrush(TextInactive),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseEnter += (_, _) => btn.Background =
            new SolidColorBrush(IsDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        btn.MouseLeave += (_, _) => btn.Background = MediaBrushes.Transparent;
        btn.MouseLeftButtonDown += (_, _) => onClick();
        return btn;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        var hwnd = new WindowInteropHelper(this).Handle;
        BarRegistry.Unregister(hwnd);
        try
        {
            if (_explorer.TryGetBarInsertionPoint(out _, out int insertY))
                _explorer.RestoreShellView(insertY);
        }
        catch { }
        base.OnClosed(e);
}
}
