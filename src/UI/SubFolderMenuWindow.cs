using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using QTBarExtension.Services;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

public class SubFolderMenuWindow : Window
{
    private readonly List<SubFolderItem>        _items;
    private readonly string                     _rootPath;
    private readonly SubFolderMenuSettings      _cfg;
    private readonly PreviewContentProvider     _preview;
    private readonly Action<string>             _onNavigate;
    private readonly Action<string,double,double> _onOpenSubmenu;

    private SubFolderMenuWindow? _childMenu;
    private SubFolderItem?       _hoveredItem;
    private Border?              _highlightedRow;
    private readonly DispatcherTimer _submenuTimer;
    private readonly DispatcherTimer _previewTimer;
    private PreviewTooltipWindow?    _previewTip;

    // ▶ボタンのBorder参照（サブメニュー開閉に使う）
    private Border?  _hoveredArrowBtn;

    // ── テーマ判定 ────────────────────────────────────────────────
    private bool IsDark => _cfg.MenuTheme == "dark" ||
        (_cfg.MenuTheme == "system" && IsSystemDark());

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

    // ── テーマカラー（カスタム優先） ──────────────────────────────
    private Color BgColor => _cfg.UseCustomColors
        ? ParseColor(_cfg.BackgroundColor)
        : (IsDark ? Color.FromArgb(245, 32, 32, 32) : Color.FromArgb(245, 245, 245, 245));

    private Color FolderColor => _cfg.UseCustomColors
        ? ParseColor(_cfg.FolderColor)
        : (IsDark ? Color.FromRgb(110, 180, 255) : Color.FromRgb(0, 80, 200));

    private Color FileColor => _cfg.UseCustomColors
        ? ParseColor(_cfg.FileColor)
        : (IsDark ? Color.FromRgb(224, 224, 224) : Color.FromRgb(30, 30, 30));

    private Color HighlightColor => _cfg.UseCustomColors
        ? ParseColor(_cfg.HighlightColor)
        : Color.FromRgb(0, 120, 212);

    private Color HeaderBorderColor => IsDark
        ? Color.FromArgb(60, 255, 255, 255)
        : Color.FromArgb(60, 0, 0, 0);

    private Color HeaderTextColor => IsDark
        ? Color.FromRgb(200, 200, 200)
        : Color.FromRgb(60, 60, 60);

    private Color EmptyTextColor => IsDark
        ? Color.FromRgb(120, 120, 120)
        : Color.FromRgb(160, 160, 160);

    private Color SizeTextColor => IsDark
        ? Color.FromRgb(130, 130, 130)
        : Color.FromRgb(150, 150, 150);

    private Color BorderOutlineColor => IsDark
        ? Color.FromRgb(70, 70, 70)
        : Color.FromRgb(180, 180, 180);

    private Color ArrowIconColor => IsDark
        ? Color.FromRgb(150, 150, 150)
        : Color.FromRgb(120, 120, 120);

    private Color MoreItemsColor => IsDark
        ? Color.FromRgb(140, 140, 140)
        : Color.FromRgb(130, 130, 130);

    public SubFolderMenuWindow(
        List<SubFolderItem> items, string rootPath,
        SubFolderMenuSettings cfg,
        PreviewContentProvider preview,
        Action<string> onNavigate,
        Action<string,double,double> onOpenSubmenu)
    {
        _items         = items;
        _rootPath      = rootPath;
        _cfg           = cfg;
        _preview       = preview;
        _onNavigate    = onNavigate;
        _onOpenSubmenu = onOpenSubmenu;

        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;
        Topmost            = true;
        ResizeMode         = ResizeMode.NoResize;
        SizeToContent      = SizeToContent.WidthAndHeight;
        Left = -9999; Top = -9999;

        _submenuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _submenuTimer.Tick += OnSubmenuTimerTick;

        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, _cfg.PreviewDelayMs))
        };
        _previewTimer.Tick += OnPreviewTimerTick;

        Content = BuildContent();

        SourceInitialized += (_, _) =>
        {
            var hwnd    = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        // Deactivated での自動クローズは行わない
        // （子メニューへのフォーカス移動で消えてしまうため）
    }

    private UIElement BuildContent()
    {
        var outer = new Border
        {
            Background      = new SolidColorBrush(BgColor),
            BorderBrush     = new SolidColorBrush(BorderOutlineColor),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Opacity         = _cfg.OpacityPercent / 100.0,
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black
            },
        };

        var root = new StackPanel();

        // ヘッダー
        var hdr = new Border
        {
            Padding         = new Thickness(10, 5, 10, 5),
            BorderBrush     = new SolidColorBrush(HeaderBorderColor),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        hdr.Child = new TextBlock
        {
            Text         = Path.GetFileName(_rootPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : _rootPath,
            FontSize     = _cfg.FontSize - 1,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = new SolidColorBrush(HeaderTextColor),
            MaxWidth     = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        root.Children.Add(hdr);

        if (_items.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text       = "（空）",
                FontSize   = _cfg.FontSize,
                Foreground = new SolidColorBrush(EmptyTextColor),
                Margin     = new Thickness(12, 6, 12, 6),
            });
        }
        else
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 480,
            };
            var list = new StackPanel();
            int show = Math.Min(_items.Count, _cfg.MaxItemsPerMenu);
            for (int i = 0; i < show; i++)
                list.Children.Add(MakeRow(_items[i]));
            if (_items.Count > _cfg.MaxItemsPerMenu)
                list.Children.Add(new Border
                {
                    Padding = new Thickness(12, 4, 12, 4),
                    Child   = new TextBlock
                    {
                        Text       = $"… さらに {_items.Count - _cfg.MaxItemsPerMenu} 件",
                        FontSize   = _cfg.FontSize - 2,
                        Foreground = new SolidColorBrush(MoreItemsColor),
                    },
                });
            scroll.Content = list;
            root.Children.Add(scroll);
        }

        outer.Child = root;
        return outer;
    }

    private UIElement MakeRow(SubFolderItem item)
    {
        var row = new Border
        {
            Padding    = new Thickness(8, 3, 8, 3),
            Cursor     = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // ▶ボタン列（クリック可能な独立した領域）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });

        var icon = MakeIcon(item);
        Grid.SetColumn(icon, 0);

        var lbl = new TextBlock
        {
            Text              = item.DisplayName,
            FontSize          = _cfg.FontSize,
            Foreground        = new SolidColorBrush(item.IsDirectory ? FolderColor : FileColor),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            MaxWidth          = 200,
            Margin            = new Thickness(4, 0, 6, 0),
        };
        Grid.SetColumn(lbl, 1);

        if (!item.IsDirectory && item.SizeBytes > 0)
        {
            var sz = new TextBlock
            {
                Text              = item.SizeDisplay,
                FontSize          = _cfg.FontSize - 2,
                Foreground        = new SolidColorBrush(SizeTextColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            };
            Grid.SetColumn(sz, 2);
            grid.Children.Add(sz);
        }

        grid.Children.Add(icon);
        grid.Children.Add(lbl);

        if (item.IsDirectory && item.HasChildren)
        {
            var arrInner = new TextBlock
            {
                Text              = "▶",
                FontSize          = 9,
                Foreground        = new SolidColorBrush(ArrowIconColor),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var arrowBtn = new Border
            {
                Width   = 20,
                Cursor  = Cursors.Hand,
                Background = Brushes.Transparent,
                Child   = arrInner,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(arrowBtn, 3);
            grid.Children.Add(arrowBtn);

            var capturedItem = item;
            arrowBtn.MouseEnter += (_, _) =>
            {
                arrInner.Foreground = new SolidColorBrush(IsDark ? Colors.White : Colors.Black);
                arrowBtn.Background = new SolidColorBrush(Color.FromArgb(80,
                    HighlightColor.R, HighlightColor.G, HighlightColor.B));
                _hoveredArrowBtn = arrowBtn;
                _hoveredItem = capturedItem;
                _submenuTimer.Stop();
                _submenuTimer.Start();
            };
            arrowBtn.MouseLeave += (_, _) =>
            {
                arrInner.Foreground = new SolidColorBrush(ArrowIconColor);
                arrowBtn.Background = Brushes.Transparent;
                if (_hoveredArrowBtn == arrowBtn)
                    _hoveredArrowBtn = null;
            };
            arrowBtn.MouseLeftButtonDown += (_, e) =>
            {
                _submenuTimer.Stop();
                // 即時サブメニュー表示
                OpenSubmenuForItem(capturedItem, arrowBtn);
                e.Handled = true;
            };
        }

        row.Child = grid;

        // 行ホバー: ハイライトのみ（サブメニューは▶ボタンから）
        row.MouseEnter += (_, _) => OnRowEnter(row, item);
        row.MouseLeave += (_, _) => OnRowLeave(row);
        row.MouseLeftButtonDown += (_, e) =>
        {
            // ▶ボタン以外の行クリック → ナビゲート（ファイルなら開く）
            OnRowClick(item);
            e.Handled = true;
        };

        return row;
    }

    private void OpenSubmenuForItem(SubFolderItem item, UIElement anchorElement)
    {
        // anchorElementの▶ボタン右端のスクリーン座標に隣接して表示
        // ウィンドウ左端からanchorの右端を計算
        Point arrowScreenPt;
        try { arrowScreenPt = anchorElement.PointToScreen(new Point(anchorElement.RenderSize.Width, 0)); }
        catch { arrowScreenPt = PointToScreen(new Point(ActualWidth, GetRowY(anchorElement))); }
        _onOpenSubmenu(item.FullPath, arrowScreenPt.X, arrowScreenPt.Y);
    }

    private UIElement MakeIcon(SubFolderItem item)
    {
        if (true) // always try system icons
        {
            var img = GetShellIcon(item.FullPath, item.IsDirectory);
            if (img != null)
                return new System.Windows.Controls.Image
                {
                    Source = img, Width = 16, Height = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                };
        }
        string icon = item.IsDirectory ? "📁" : item.IsZip ? "🗜" : "📄";
        return new TextBlock
        {
            Text = icon, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static ImageSource? GetShellIcon(string path, bool isDir)
    {
        try
        {
            var shfi  = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_SMALLICON;
            uint attr  = 0;
            bool exists = isDir ? Directory.Exists(path) : File.Exists(path);
            if (!exists) { flags |= SHGFI_USEFILEATTRIBUTES; attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : 0; }
            IntPtr hr = SHGetFileInfo(path, attr, ref shfi,
                (uint)Marshal.SizeOf(shfi), flags);
            if (hr == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(
                shfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            DestroyIcon(shfi.hIcon);
            return src;
        }
        catch { return null; }
    }

    private void OnRowEnter(Border row, SubFolderItem item)
    {
        if (_highlightedRow != null)
            _highlightedRow.Background = Brushes.Transparent;
        row.Background = new SolidColorBrush(Color.FromArgb(80,
            HighlightColor.R, HighlightColor.G, HighlightColor.B));
        _highlightedRow = row;
        _hoveredItem    = item;

        // 行ホバーではサブメニューは開かない（▶ボタン担当）
        // ただしフォルダでない場合はpreviewタイマーのみ
        _previewTimer.Stop();
        HidePreviewTip();
        if (_cfg.TooltipPreview && !item.IsDirectory) _previewTimer.Start();
    }

    private void OnRowLeave(Border row)
    {
        if (_highlightedRow == row)
        {
            row.Background  = Brushes.Transparent;
            _highlightedRow = null;
        }
        _previewTimer.Stop();
        HidePreviewTip();
    }

    private void OnRowClick(SubFolderItem item)
    {
        if (item.IsDirectory) { _onNavigate(item.FullPath); CloseAll(); }
        else
        {
            try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(item.FullPath)
                { UseShellExecute = true }); }
            catch { }
            CloseAll();
        }
    }

    private void OnSubmenuTimerTick(object? s, EventArgs e)
    {
        _submenuTimer.Stop();
        if (_hoveredItem == null || !_hoveredItem.IsDirectory || _hoveredArrowBtn == null) return;
        OpenSubmenuForItem(_hoveredItem, _hoveredArrowBtn);
    }

    private void OnPreviewTimerTick(object? s, EventArgs e)
    {
        _previewTimer.Stop();
        if (_hoveredItem == null || _hoveredItem.IsDirectory) return;
        var item = _hoveredItem;
        System.Threading.Tasks.Task.Run(() =>
        {
            var info = _preview.Load(item.FullPath);
            Dispatcher.InvokeAsync(() =>
            {
                if (_hoveredItem != item || info == null) return;
                var pt = PointToScreen(new Point(ActualWidth + 4, 0));
                _previewTip ??= new PreviewTooltipWindow(_preview.Settings, _cfg);
                _previewTip.ShowInfo(info, pt.X, pt.Y);
            });
        });
    }

    private double GetRowY(UIElement el)
    {
        try { return el.TranslatePoint(new Point(0, 0), this).Y; } catch { return 0; }
    }

    private void HidePreviewTip() { _previewTip?.HideWindow(); }

    /// <summary>サービスから子メニューをセットされたとき、前の子を閉じて登録する</summary>
    public void SetChildMenu(SubFolderMenuWindow child)
    {
        CloseChildMenu();
        _childMenu = child;
        child.Closed += (_, _) => { if (_childMenu == child) _childMenu = null; };
    }

    public void ShowAt(double screenX, double screenY)
    {
        var screen = SystemParameters.WorkArea;
        Left = screenX; Top = screenY;
        Show(); UpdateLayout();
        if (Left + ActualWidth  > screen.Right)  Left = screen.Right  - ActualWidth  - 4;
        if (Top  + ActualHeight > screen.Bottom) Top  = Math.Max(screen.Top, screen.Bottom - ActualHeight);
    }

    public void CloseAll()
    {
        _submenuTimer.Stop(); _previewTimer.Stop();
        HidePreviewTip(); CloseChildMenu();
        try { Close(); } catch { }
    }

    private void CloseChildMenu()  { try { _childMenu?.CloseAll(); } catch { } _childMenu = null; }

    protected override void OnClosed(EventArgs e)
    {
        _submenuTimer.Stop(); _previewTimer.Stop();
        HidePreviewTip(); CloseChildMenu();
        base.OnClosed(e);
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8)
                return Color.FromArgb(
                    Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
            if (hex.Length == 6)
                return Color.FromRgb(
                    Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        }
        catch { }
        return Color.FromRgb(45, 45, 45);
    }
}

/// <summary>ファイルホバー時の簡易プレビューツールチップ（サイズは PreviewSettings に従う）</summary>
public class PreviewTooltipWindow : Window
{
    private readonly System.Windows.Controls.Image _img;
    private readonly TextBlock _info;
    private readonly double _maxW;
    private readonly double _maxH;
    private readonly SubFolderMenuSettings? _menuCfg;

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

    private bool IsDark => _menuCfg != null
        ? (_menuCfg.MenuTheme == "dark" || (_menuCfg.MenuTheme == "system" && IsSystemDark()))
        : IsSystemDark();

    private Color TooltipBg   => IsDark ? Color.FromArgb(245, 30, 30, 30)   : Color.FromArgb(245, 245, 245, 245);
    private Color TooltipBdr  => IsDark ? Color.FromRgb(80, 80, 80)          : Color.FromRgb(180, 180, 180);
    private Color TooltipText => IsDark ? Color.FromRgb(220, 220, 220)       : Color.FromRgb(30, 30, 30);

    public PreviewTooltipWindow(
        QTBarExtension.Models.PreviewSettings? settings = null,
        SubFolderMenuSettings? menuCfg = null)
    {
        _maxW    = settings?.ImageMaxWidth  ?? 512;
        _maxH    = settings?.ImageMaxHeight ?? 256;
        _menuCfg = menuCfg;

        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false;
        Topmost = true; ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false; SizeToContent = SizeToContent.WidthAndHeight;
        Left = -9999; Top = -9999;

        var border = new Border
        {
            Background      = new SolidColorBrush(TooltipBg),
            BorderBrush     = new SolidColorBrush(TooltipBdr),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(6),
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8, ShadowDepth = 2, Opacity = 0.4, Color = Colors.Black
            },
        };
        var panel = new StackPanel();
        _img = new System.Windows.Controls.Image
        {
            MaxWidth = _maxW, MaxHeight = _maxH, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed,
        };
        _info = new TextBlock
        {
            FontSize = 11, Foreground = new SolidColorBrush(TooltipText),
            MaxWidth = _maxW, TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_img); panel.Children.Add(_info);
        border.Child = panel; Content = border;

        SourceInitialized += (_, _) =>
        {
            var hwnd    = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    public void ShowInfo(PreviewInfo info, double x, double y)
    {
        if (info.Image != null)
        {
            // アスペクト比を保ってリサイズ
            double iw = info.Image.PixelWidth, ih = info.Image.PixelHeight;
            if (iw > 0 && ih > 0)
            {
                double scale = Math.Min(Math.Min(_maxW / iw, _maxH / ih), 1.0);
                _img.Width  = Math.Floor(iw * scale);
                _img.Height = Math.Floor(ih * scale);
                _img.MaxWidth  = _maxW;
                _img.MaxHeight = _maxH;
            }
            _img.Source = info.Image;
            _img.Visibility = Visibility.Visible;
        }
        else
        {
            _img.Source = null;
            _img.Visibility = Visibility.Collapsed;
        }

        var parts = new System.Collections.Generic.List<string> { info.FileName };
        if (info.SizeBytes > 0) parts.Add(FormatSize(info.SizeBytes));
        if (info.Modified.HasValue) parts.Add(info.Modified.Value.ToString("yyyy/MM/dd HH:mm"));
        if (!string.IsNullOrEmpty(info.Dimensions)) parts.Add(info.Dimensions);
        _info.Text = string.Join("  |  ", parts);

        // 画面外に出ないようにクランプ
        Left = x; Top = y;
        if (!IsVisible) Show();
        UpdateLayout();
        var screen = SystemParameters.WorkArea;
        if (Left + ActualWidth  > screen.Right)  Left = screen.Right  - ActualWidth  - 4;
        if (Top  + ActualHeight > screen.Bottom) Top  = screen.Bottom - ActualHeight - 4;
        if (Left < screen.Left) Left = screen.Left + 4;
        if (Top  < screen.Top)  Top  = screen.Top  + 4;
    }

    public void HideWindow() { if (IsVisible) Hide(); }

    private static string FormatSize(long b)
    {
        if (b < 1024)        return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:0.#} KB";
        return $"{b / (1024.0 * 1024):0.#} MB";
    }
}
