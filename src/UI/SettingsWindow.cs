using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QTBarExtension.Models;
using QTBarExtension.Services;

namespace QTBarExtension.UI;

public class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action      _save;
    private readonly Action      _refresh;
    private readonly PreviewContentProvider? _previewProvider;

    public SettingsWindow(AppSettings settings, Action save, Action refresh,
        PreviewContentProvider? previewProvider = null)
    {
        _settings = settings;
        _save     = save;
        _refresh  = refresh;
        _previewProvider = previewProvider;

        Title  = "QTBarExtension - 設定";
        Width  = 560;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tab = new TabControl { Margin = new Thickness(8) };
        tab.Items.Add(MakeGeneralTab());
        tab.Items.Add(MakeGroupsTab());
        tab.Items.Add(MakeHistoryTab());
        tab.Items.Add(MakeSubFolderMenuTab());
        tab.Items.Add(MakePreviewGeneralTab());
        tab.Items.Add(MakePreviewExtensionsTab());
        tab.Items.Add(MakePreviewWindowTab());
        Content = tab;
    }

    // ── タブグループ管理 ─────────────────────────────────────────
    private TabItem MakeGroupsTab()
    {
        var item  = new TabItem { Header = "タブグループ" };
        var panel = new StackPanel { Margin = new Thickness(8) };

        foreach (var group in _settings.TabGroups)
        {
            var g   = group;
            var clr = GroupEditDialog.HexToColor(g.Color);

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };

            var swatch = new Border
            {
                Width = 16, Height = 16,
                Background = new SolidColorBrush(clr),
                BorderBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var editBtn = new Button { Content = "編集", Width = 50, Margin = new Thickness(4, 0, 0, 0) };
            editBtn.Click += (_, _) =>
            {
                var dlg = new GroupEditDialog(g);
                if (dlg.ShowDialog() == true && dlg.Result != null)
                {
                    g.Name = dlg.Result.Name; g.Color = dlg.Result.Color;
                    _save(); Reload();
                }
            };

            var delBtn = new Button { Content = "削除", Width = 50, Margin = new Thickness(4, 0, 0, 0), Foreground = Brushes.DarkRed };
            delBtn.Click += (_, _) =>
            {
                if (MessageBox.Show($"グループ「{g.Name}」を削除しますか？", "確認",
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    _settings.TabGroups.Remove(g);
                    _save(); Reload();
                }
            };

            DockPanel.SetDock(editBtn, Dock.Right);
            DockPanel.SetDock(delBtn,  Dock.Right);
            header.Children.Add(swatch);
            header.Children.Add(delBtn);
            header.Children.Add(editBtn);
            header.Children.Add(new TextBlock
            {
                Text = g.Name, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var box = new GroupBox
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(4),
                Header = header,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, clr.R, clr.G, clr.B)),
                BorderThickness = new Thickness(1.5),
            };
            var inner = new StackPanel();

            foreach (var t in g.Tabs)
            {
                var entry = t;
                var row   = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var del   = new Button { Content = "×", Width = 22, Height = 22, Margin = new Thickness(4, 0, 0, 0) };
                del.Click += (_, _) => { g.Tabs.Remove(entry); _save(); Reload(); };
                DockPanel.SetDock(del, Dock.Right);
                string name = Path.GetFileName(
                    Uri.UnescapeDataString(entry.Path.Replace("file:///", "").Replace('/', '\\')));
                row.Children.Add(del);
                row.Children.Add(new TextBlock
                {
                    Text = $"📁 {(string.IsNullOrEmpty(name) ? entry.Path : name)}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = entry.Path,
                });
                inner.Children.Add(row);
            }

            var addRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var pathBox = new TextBox { Width = 180, Margin = new Thickness(0, 0, 4, 0), FontSize = 11 };
            var browse  = new Button { Content = "参照...", Width = 60, Margin = new Thickness(0, 0, 4, 0) };
            browse.Click += (_, _) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    pathBox.Text = dlg.SelectedPath;
            };
            var addBtn = new Button { Content = "追加", Width = 46 };
            addBtn.Click += (_, _) =>
            {
                var p = pathBox.Text.Trim();
                if (Directory.Exists(p))
                {
                    g.Tabs.Add(new TabEntry { Path = p });
                    _save(); Reload();
                }
            };
            addRow.Children.Add(pathBox);
            addRow.Children.Add(browse);
            addRow.Children.Add(addBtn);
            inner.Children.Add(addRow);

            box.Content = inner;
            panel.Children.Add(box);
        }

        var newGrpBtn = new Button
        {
            Content = "＋ 新しいグループを作成",
            Margin  = new Thickness(0, 4, 0, 0),
        };
        newGrpBtn.Click += (_, _) =>
        {
            var dlg = new GroupEditDialog(null);
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                _settings.TabGroups.Add(dlg.Result);
                _save(); Reload();
            }
        };
        panel.Children.Add(newGrpBtn);

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    // ── 履歴タブ ────────────────────────────────────────────────
    private TabItem MakeHistoryTab()
    {
        var item  = new TabItem { Header = "履歴" };
        var panel = new StackPanel { Margin = new Thickness(8) };

        var clear = new Button { Content = "履歴をすべてクリア", Width = 140, Margin = new Thickness(0, 0, 0, 8) };
        clear.Click += (_, _) => { _settings.History.Clear(); _save(); Reload(); };
        panel.Children.Add(clear);

        foreach (var h in _settings.History)
        {
            string name = Path.GetFileName(
                Uri.UnescapeDataString(h.Path.Replace("file:///", "").Replace('/', '\\')));
            panel.Children.Add(new TextBlock
            {
                Text       = $"{(string.IsNullOrEmpty(name) ? h.Path : name)}  ({h.VisitedAt:MM/dd HH:mm})",
                Foreground = Brushes.DimGray,
                FontSize   = 11,
                Margin     = new Thickness(0, 1, 0, 1),
                ToolTip    = h.Path,
            });
        }

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    // ── 一般設定タブ ─────────────────────────────────────────────
    private TabItem MakeGeneralTab()
    {
        var item  = new TabItem { Header = "一般" };
        var panel = new StackPanel { Margin = new Thickness(12) };

        // タブバー自体の表示/非表示（デフォルトON）
        var showTabBar = new CheckBox
        {
            Content   = "タブバーを表示する",
            IsChecked = _settings.ShowTabBar,
            Margin    = new Thickness(0, 0, 0, 8),
        };
        showTabBar.Checked   += (_, _) =>
        {
            _settings.ShowTabBar = true;
            _save();
            QTBarExtension.App.ApplyTabBarVisibility();
        };
        showTabBar.Unchecked += (_, _) =>
        {
            _settings.ShowTabBar = false;
            _save();
            QTBarExtension.App.ApplyTabBarVisibility();
        };

        // 自動起動の有効/無効はレジストリ（Core.StartupRegistration）を唯一の正とする。
        // トレイ右クリックメニューの「Windows起動時に自動実行」と状態を共有するため、
        // ここでも実際のレジストリ状態を読み取って表示する（AppSettings.StartWithWindowsは参照しない）。
        var startup = new CheckBox
        {
            Content   = "Windowsスタートアップ時に自動起動",
            IsChecked = QTBarExtension.Core.StartupRegistration.IsEnabled(),
            Margin    = new Thickness(0, 0, 0, 8),
        };
        startup.Checked   += (_, _) => SetStartup(true,  startup);
        startup.Unchecked += (_, _) => SetStartup(false, startup);

        // テーマ選択
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        themeRow.Children.Add(new TextBlock
        {
            Text = "テーマ:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var themeBox = new ComboBox { Width = 100 };
        themeBox.Items.Add("システム設定に合わせる");
        themeBox.Items.Add("ライト");
        themeBox.Items.Add("ダーク");
        themeBox.SelectedIndex = _settings.Theme switch
        {
            "light"  => 1,
            "dark"   => 2,
            _        => 0,
        };
        themeBox.SelectionChanged += (_, _) =>
        {
            _settings.Theme = themeBox.SelectedIndex switch
            {
                1 => "light",
                2 => "dark",
                _ => "system",
            };
            _save(); _refresh();
        };
        themeRow.Children.Add(themeBox);

        // バーの高さ
        var heightRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        heightRow.Children.Add(new TextBlock
        {
            Text = "バーの高さ (24〜48px):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var heightBox = new TextBox { Text = _settings.BarHeight.ToString(), Width = 50 };
        heightBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(heightBox.Text, out int h) && h is >= 24 and <= 48)
            {
                _settings.BarHeight = h;
                _save(); _refresh();
            }
        };
        heightRow.Children.Add(heightBox);

        // 新タブボタン動作
        var newTabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        newTabRow.Children.Add(new TextBlock
        {
            Text = "＋ボタンの動作:", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var newTabBox = new ComboBox { Width = 160 };
        newTabBox.Items.Add("現在のタブを複製（デフォルト）");
        newTabBox.Items.Add("ホームフォルダで新規タブ");
        newTabBox.SelectedIndex = _settings.NewTabAction == "home" ? 1 : 0;
        newTabBox.SelectionChanged += (_, _) =>
        {
            _settings.NewTabAction = newTabBox.SelectedIndex == 1 ? "home" : "duplicate";
            _save(); _refresh();
        };
        newTabRow.Children.Add(newTabBox);

        // ── フォルダビュー拡張 ──────────────────────────
        var folderViewHeader = new TextBlock
        {
            Text = "フォルダビュー拡張",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4),
        };

        var shiftWheel = new CheckBox
        {
            Content = "Shift+ホイールで横スクロール",
            IsChecked = _settings.EnableShiftWheelHorizontalScroll,
            Margin = new Thickness(0, 0, 0, 4),
        };
        shiftWheel.Checked   += (_, _) => { _settings.EnableShiftWheelHorizontalScroll = true;  _save(); };
        shiftWheel.Unchecked += (_, _) => { _settings.EnableShiftWheelHorizontalScroll = false; _save(); };

        var fullNameTip = new CheckBox
        {
            Content = "詳細表示で省略されたファイル名をホバーで完全表示",
            IsChecked = _settings.EnableFullNameTooltip,
            Margin = new Thickness(0, 0, 0, 4),
        };
        fullNameTip.Checked   += (_, _) => { _settings.EnableFullNameTooltip = true;  _save(); };
        fullNameTip.Unchecked += (_, _) => { _settings.EnableFullNameTooltip = false; _save(); };

        var version = new TextBlock
        {
            Text = "QTBarExtension v1.1  |  .NET 10 LTS  |  Windows 11 対応",
            FontSize = 10, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 16, 0, 0),
        };

        panel.Children.Add(showTabBar);
        panel.Children.Add(startup);
        panel.Children.Add(themeRow);
        panel.Children.Add(heightRow);
        panel.Children.Add(newTabRow);
        panel.Children.Add(folderViewHeader);
        panel.Children.Add(shiftWheel);
        panel.Children.Add(fullNameTip);
        panel.Children.Add(version);

        item.Content = panel;
        return item;
    }

    // ── サブフォルダメニュータブ ──────────────────────────────────────
    private TabItem MakeSubFolderMenuTab()
    {
        var item  = new TabItem { Header = "サブフォルダメニュー" };
        var s     = _settings.SubFolderMenu;
        var panel = new StackPanel { Margin = new Thickness(12) };

        // 有効/無効
        var enabled = new CheckBox
        {
            Content   = "サブフォルダメニューを有効にする",
            IsChecked = s.Enabled,
            Margin    = new Thickness(0, 0, 0, 8),
            FontWeight = FontWeights.SemiBold,
        };
        enabled.Checked   += (_, _) => { s.Enabled = true;  _save(); };
        enabled.Unchecked += (_, _) => { s.Enabled = false; _save(); };
        panel.Children.Add(enabled);

        panel.Children.Add(new TextBlock
        {
            Text = "表示内容",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 4),
        });

        MakeCheck(panel, "タブ上でサブフォルダチップを有効にする（タブのアイコンクリックで展開）",
            s.EnableTabIconSubFolder, v => { s.EnableTabIconSubFolder = v; _save(); });
        MakeCheck(panel, "フォルダビュー上にサブフォルダチップを表示する",
            s.ShowFolderViewChip, v => { s.ShowFolderViewChip = v; _save(); });
        MakeCheck(panel, "ファイルを表示する",              s.ShowFiles,     v => { s.ShowFiles     = v; _save(); });
        MakeCheck(panel, "隠し属性のオブジェクトを表示する", s.ShowHidden,    v => { s.ShowHidden    = v; _save(); });
        MakeCheck(panel, "システム属性のオブジェクトを表示する", s.ShowSystem, v => { s.ShowSystem   = v; _save(); });
        MakeCheck(panel, "ツールチップでプレビューする",      s.TooltipPreview, v => { s.TooltipPreview = v; _save(); });
        MakeCheck(panel, "フォルダウィンドウが非アクティブでも表示する", s.ShowWhenInactive, v => { s.ShowWhenInactive = v; _save(); });
        MakeCheck(panel, "グループファイルはルートに展開する", s.ExpandGroupFiles, v => { s.ExpandGroupFiles = v; _save(); });
        MakeCheck(panel, "ドラッグ＆ドロップ中にマウス下のフォルダのメニューを表示する", s.ShowOnDragOver, v => { s.ShowOnDragOver = v; _save(); });
        MakeCheck(panel, "圧縮フォルダ（zip等）に対してもメニューを有効にする", s.EnableForZip, v => { s.EnableForZip = v; _save(); });
        MakeCheck(panel, "ライブラリはフォルダごとにソートする", s.SortLibraryByFolder, v => { s.SortLibraryByFolder = v; _save(); });
        // カラーテーマ選択（「カスタムカラー」を含む）
        var menuThemeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        menuThemeRow.Children.Add(new TextBlock
        {
            Text = "メニューのカラーテーマ:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var menuThemeBox = new ComboBox { Width = 160 };
        menuThemeBox.Items.Add("システム設定に合わせる");
        menuThemeBox.Items.Add("ダーク");
        menuThemeBox.Items.Add("ライト");
        menuThemeBox.Items.Add("カスタムカラー");
        // UseCustomColors=true なら index=3、それ以外はMenuThemeで判定
        menuThemeBox.SelectedIndex = s.UseCustomColors ? 3 : s.MenuTheme switch
        {
            "dark"  => 1,
            "light" => 2,
            _       => 0,
        };

        // カスタムカラー入力欄（カスタムカラー選択時のみ表示）
        var customColorPanel = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = s.UseCustomColors ? Visibility.Visible : Visibility.Collapsed,
        };
        customColorPanel.Children.Add(MakeColorRow("背景色 (#AARRGGBB):",      s.BackgroundColor, v => { s.BackgroundColor = v; _save(); }));
        customColorPanel.Children.Add(MakeColorRow("フォルダ色 (#AARRGGBB):",  s.FolderColor,     v => { s.FolderColor     = v; _save(); }));
        customColorPanel.Children.Add(MakeColorRow("ファイル色 (#AARRGGBB):",  s.FileColor,       v => { s.FileColor       = v; _save(); }));
        customColorPanel.Children.Add(MakeColorRow("ハイライト色 (#AARRGGBB):", s.HighlightColor, v => { s.HighlightColor  = v; _save(); }));

        menuThemeBox.SelectionChanged += (_, _) =>
        {
            bool isCustom = menuThemeBox.SelectedIndex == 3;
            s.UseCustomColors = isCustom;
            s.MenuTheme = menuThemeBox.SelectedIndex switch
            {
                1 => "dark",
                2 => "light",
                _ => "system",
            };
            customColorPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            _save();
        };
        menuThemeRow.Children.Add(menuThemeBox);
        panel.Children.Add(menuThemeRow);
        panel.Children.Add(customColorPanel);

        // ソート
        var sortHeader = new TextBlock
        {
            Text = "表示設定",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
        };
        panel.Children.Add(sortHeader);

        var sortRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sortRow.Children.Add(new TextBlock { Text = "アイテムのソート方法:", Width = 160, VerticalAlignment = VerticalAlignment.Center });
        var sortBox = new ComboBox { Width = 160 };
        sortBox.Items.Add("拡張子（デフォルト）");
        sortBox.Items.Add("名前");
        sortBox.Items.Add("更新日時（新しいのが上）");
        sortBox.Items.Add("更新日時（古いのが上）");
        sortBox.Items.Add("なし（ファイルシステム順）");
        sortBox.SelectedIndex = s.SortMode switch
        {
            Models.SubFolderSortMode.Name         => 1,
            Models.SubFolderSortMode.ModifiedDesc => 2,
            Models.SubFolderSortMode.ModifiedAsc  => 3,
            Models.SubFolderSortMode.None         => 4,
            _                                     => 0,
        };
        sortBox.SelectionChanged += (_, _) =>
        {
            s.SortMode = sortBox.SelectedIndex switch
            {
                1 => Models.SubFolderSortMode.Name,
                2 => Models.SubFolderSortMode.ModifiedDesc,
                3 => Models.SubFolderSortMode.ModifiedAsc,
                4 => Models.SubFolderSortMode.None,
                _ => Models.SubFolderSortMode.Extension,
            };
            _save();
        };
        sortRow.Children.Add(sortBox);
        panel.Children.Add(sortRow);

        panel.Children.Add(MakeIntRow("メニューの不透明度 (%):",                   s.OpacityPercent,   1, 100,  v => { s.OpacityPercent  = v; _save(); }));
        panel.Children.Add(MakeIntRow("ファイルドラッグでメニューが表示されるまでの時間 (ms):", s.DragOpenDelayMs, 0, 5000, v => { s.DragOpenDelayMs = v; _save(); }));
        panel.Children.Add(MakeIntRow("プレビューが表示されるまでの時間 (ms):",     s.PreviewDelayMs,   0, 5000, v => { s.PreviewDelayMs  = v; _save(); }));
        panel.Children.Add(MakeIntRow("1メニューの最大アイテム数:",                 s.MaxItemsPerMenu,  5, 200,  v => { s.MaxItemsPerMenu = v; _save(); }));
        panel.Children.Add(MakeIntRow("フォントサイズ:",                            (int)s.FontSize,    8, 24,   v => { s.FontSize        = v; _save(); }));

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    private void MakeCheck(StackPanel panel, string label, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content   = label,
            IsChecked = initial,
            Margin    = new Thickness(0, 0, 0, 4),
        };
        cb.Checked   += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        panel.Children.Add(cb);
    }

    private static UIElement MakeColorRow(string label, string initial, Action<string> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = initial, Width = 100 };
        box.LostFocus += (_, _) =>
        {
            // 簡易バリデーション: #AARRGGBB or #RRGGBB
            var t = box.Text.Trim();
            if (t.StartsWith('#') && (t.Length == 7 || t.Length == 9))
                onChange(t);
            else
                box.Text = initial;
        };
        row.Children.Add(box);
        return row;
    }

    // ── プレビュー: 全般タブ ─────────────────────────────────────
    private TabItem MakePreviewGeneralTab()
    {
        var item  = new TabItem { Header = "プレビュー: 全般" };
        var p     = _settings.Preview;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var enabled = new CheckBox
        {
            Content = "プレビュー機能を有効にする",
            IsChecked = p.Enabled,
            Margin = new Thickness(0, 0, 0, 8),
        };
        enabled.Checked   += (_, _) => { p.Enabled = true;  _save(); };
        enabled.Unchecked += (_, _) => { p.Enabled = false; _save(); };
        panel.Children.Add(enabled);

        // プレビューウィンドウのテーマ
        var previewThemeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        previewThemeRow.Children.Add(new TextBlock
        {
            Text = "プレビューウィンドウのカラー:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var previewThemeBox = new ComboBox { Width = 160 };
        previewThemeBox.Items.Add("システム設定に合わせる");
        previewThemeBox.Items.Add("ダーク");
        previewThemeBox.Items.Add("ライト");
        previewThemeBox.SelectedIndex = p.PreviewTheme switch
        {
            "dark"  => 1,
            "light" => 2,
            _       => 0,
        };
        previewThemeBox.SelectionChanged += (_, _) =>
        {
            p.PreviewTheme = previewThemeBox.SelectedIndex switch
            {
                1 => "dark",
                2 => "light",
                _ => "system",
            };
            _save();
        };
        previewThemeRow.Children.Add(previewThemeBox);
        panel.Children.Add(previewThemeRow);

        // 表示までの待機時間
        panel.Children.Add(MakeIntRow("表示までの待機時間 (ms):", p.HoverDelayMs, 0, 5000,
            v => { p.HoverDelayMs = v; _save(); }));

        // 不透明度
        panel.Children.Add(MakeIntRow("プレビューウィンドウの不透明度 (%):", p.OpacityPercent, 10, 100,
            v => { p.OpacityPercent = v; _save(); }));

        // 画像キャッシュ最大サイズ
        panel.Children.Add(MakeIntRow("画像キャッシュの最大サイズ (MiB):",  p.ImageCacheMaxMiB, 16, 4096,
            v => { p.ImageCacheMaxMiB = v; _save(); }));

        // キャッシュ注記
        panel.Children.Add(new TextBlock
        {
            Text = "※ フォルダ内・圧縮フォルダ内のプレビューは画像枚数が多くなりがちです。\n" +
                   "  メモリ使用量が気になる場合は上限を小さく設定するか、下のボタンでクリアしてください。",
            FontSize = 10,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 4),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });

        // キャッシュ使用量表示 + クリアボタン
        var cacheUsageRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        string usageText = _previewProvider != null
            ? $"現在の使用量: {_previewProvider.CacheUsageBytes / 1024.0 / 1024.0:0.##} MiB  ({_previewProvider.CacheEntryCount} 件)"
            : "現在の使用量: -";
        var cacheUsageLabel = new TextBlock
        {
            Text = usageText,
            FontSize = 10,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var clearCacheBtn = new Button
        {
            Content = "画像キャッシュをクリア",
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        clearCacheBtn.Click += (_, _) =>
        {
            _previewProvider?.ClearCache();
            cacheUsageLabel.Text = "現在の使用量: 0 MiB  (0 件)";
            MessageBox.Show("画像キャッシュをクリアしました。", "QTBarExtension",
                MessageBoxButton.OK, MessageBoxImage.Information);
        };
        cacheUsageRow.Children.Add(clearCacheBtn);
        cacheUsageRow.Children.Add(cacheUsageLabel);
        panel.Children.Add(cacheUsageRow);

        // 非アクティブ時も表示
        var inactive = new CheckBox
        {
            Content = "フォルダウィンドウが非アクティブでもプレビューを表示する",
            IsChecked = p.ShowWhenInactive,
            Margin = new Thickness(0, 0, 0, 4),
        };
        inactive.Checked   += (_, _) => { p.ShowWhenInactive = true;  _save(); };
        inactive.Unchecked += (_, _) => { p.ShowWhenInactive = false; _save(); };
        panel.Children.Add(inactive);

        // ネットワークファイル
        var network = new CheckBox
        {
            Content = "ネットワーク上のファイル (\\\\server\\...) もプレビューする",
            IsChecked = p.PreviewNetworkFiles,
            Margin = new Thickness(0, 0, 0, 4),
        };
        network.Checked   += (_, _) => { p.PreviewNetworkFiles = true;  _save(); };
        network.Unchecked += (_, _) => { p.PreviewNetworkFiles = false; _save(); };
        panel.Children.Add(network);

        // フォルダ内コンテンツプレビュー
        var folderContents = new CheckBox
        {
            Content = "フォルダ内のファイルもプレビューする（フォルダホバー時）",
            IsChecked = p.PreviewFolderContents,
            Margin = new Thickness(0, 0, 0, 4),
        };
        folderContents.Checked   += (_, _) => { p.PreviewFolderContents = true;  _save(); };
        folderContents.Unchecked += (_, _) => { p.PreviewFolderContents = false; _save(); };
        panel.Children.Add(folderContents);

        // アーカイブ内ファイル（圧縮フォルダ自体のホバー＋内部エントリのホバー、両方を制御）
        var archive = new CheckBox
        {
            Content = "圧縮フォルダ（zip等）内のファイルもプレビューする",
            IsChecked = p.PreviewArchiveContents,
            Margin = new Thickness(0, 0, 0, 4),
        };
        archive.Checked   += (_, _) => { p.PreviewArchiveContents = true;  _save(); };
        archive.Unchecked += (_, _) => { p.PreviewArchiveContents = false; _save(); };
        panel.Children.Add(archive);

        // 詳細表示ではフォルダをプレビューしない（archiveの下、入れ子なし）
        var noFolderDetailView = new CheckBox
        {
            Content = "詳細表示ではフォルダ・圧縮フォルダをプレビューしない",
            IsChecked = p.NoFolderPreviewInDetailView,
            Margin = new Thickness(0, 0, 0, 4),
        };
        noFolderDetailView.Checked   += (_, _) => { p.NoFolderPreviewInDetailView = true;  _save(); };
        noFolderDetailView.Unchecked += (_, _) => { p.NoFolderPreviewInDetailView = false; _save(); };
        panel.Children.Add(noFolderDetailView);

        // ハードウェアアクセラレーション
        var hwAccel = new CheckBox
        {
            Content = "可能であればハードウェアアクセラレーションを使用する",
            IsChecked = p.UseHardwareAcceleration,
            Margin = new Thickness(0, 0, 0, 4),
        };
        hwAccel.Checked   += (_, _) => { p.UseHardwareAcceleration = true;  _save(); };
        hwAccel.Unchecked += (_, _) => { p.UseHardwareAcceleration = false; _save(); };
        panel.Children.Add(hwAccel);

        // 再生位置記憶
        var rememberPos = new CheckBox
        {
            Content = "動画・音声の再生位置を記録する",
            IsChecked = p.RememberPlaybackPosition,
            Margin = new Thickness(0, 0, 0, 4),
        };
        rememberPos.Checked   += (_, _) => { p.RememberPlaybackPosition = true;  _save(); };
        rememberPos.Unchecked += (_, _) => { p.RememberPlaybackPosition = false; _save(); };
        panel.Children.Add(rememberPos);

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    // ── プレビュー: 拡張子・フォント・色タブ ──────────────────────
    private TabItem MakePreviewExtensionsTab()
    {
        var item  = new TabItem { Header = "プレビュー: 拡張子/フォント" };
        var p     = _settings.Preview;
        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(new TextBlock
        {
            Text = "拡張子はカンマ区切りで指定します（例: .jpg, .png）",
            FontSize = 10, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(MakeExtensionRow("画像の拡張子:", p.ImageExtensions,
            list => { p.ImageExtensions = list; _save(); }));
        panel.Children.Add(MakeExtensionRow("動画の拡張子:", p.VideoExtensions,
            list => { p.VideoExtensions = list; _save(); }));
        panel.Children.Add(MakeExtensionRow("音声の拡張子:", p.AudioExtensions,
            list => { p.AudioExtensions = list; _save(); }));
        panel.Children.Add(MakeExtensionRow("テキストの拡張子:", p.TextExtensions,
            list => { p.TextExtensions = list; _save(); }));

        // テキストプレビューのフォント・色設定
        panel.Children.Add(new TextBlock
        {
            Text = "※ 文字色・背景色に \"auto\" と入力するとプレビューテーマに合わせて自動調整されます。",
            FontSize = 10, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 8, 0, 4),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });
        var fontHeader = new TextBlock
        {
            Text = "テキストプレビュー",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 4),
        };
        panel.Children.Add(fontHeader);

        var fontRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        fontRow.Children.Add(new TextBlock { Text = "フォント:", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        var fontBox = new TextBox { Text = p.TextFontFamily, Width = 160 };
        fontBox.LostFocus += (_, _) => { p.TextFontFamily = fontBox.Text.Trim() is { Length: > 0 } f ? f : "Consolas"; _save(); };
        fontRow.Children.Add(fontBox);
        panel.Children.Add(fontRow);

        var fontSizeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        fontSizeRow.Children.Add(new TextBlock { Text = "フォントサイズ:", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        var fontSizeBox = new TextBox { Text = p.TextFontSize.ToString("0.#"), Width = 60 };
        fontSizeBox.LostFocus += (_, _) =>
        {
            if (double.TryParse(fontSizeBox.Text, out double v) && v is >= 6 and <= 48)
            { p.TextFontSize = v; _save(); }
        };
        fontSizeRow.Children.Add(fontSizeBox);
        panel.Children.Add(fontSizeRow);

        var fgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        fgRow.Children.Add(new TextBlock { Text = "文字色 (#RRGGBB):", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        var fgBox = new TextBox { Text = p.TextForegroundColor, Width = 100 };
        fgBox.LostFocus += (_, _) =>
        {
            string v = fgBox.Text.Trim();
            if (v == "auto" || IsValidHexColor(v)) { p.TextForegroundColor = v; _save(); }
        };
        fgRow.Children.Add(fgBox);
        panel.Children.Add(fgRow);

        var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        bgRow.Children.Add(new TextBlock { Text = "背景色 (#RRGGBB):", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        var bgBox = new TextBox { Text = p.TextBackgroundColor, Width = 100 };
        bgBox.LostFocus += (_, _) =>
        {
            string v = bgBox.Text.Trim();
            if (v == "auto" || IsValidHexColor(v)) { p.TextBackgroundColor = v; _save(); }
        };
        bgRow.Children.Add(bgBox);
        panel.Children.Add(bgRow);

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    // ── プレビュー: ウィンドウ設定タブ ─────────────────────────────
    private TabItem MakePreviewWindowTab()
    {
        var item  = new TabItem { Header = "プレビュー: ウィンドウ" };
        var p     = _settings.Preview;
        var panel = new StackPanel { Margin = new Thickness(12) };

        var imgHeader = new TextBlock { Text = "画像", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(imgHeader);
        panel.Children.Add(MakeIntRow("最大幅 (px):",  p.ImageMaxWidth,  64, 2048, v => { p.ImageMaxWidth  = v; _save(); }));
        panel.Children.Add(MakeIntRow("最大高 (px):",  p.ImageMaxHeight, 64, 2048, v => { p.ImageMaxHeight = v; _save(); }));

        var vidHeader = new TextBlock { Text = "動画", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(vidHeader);
        panel.Children.Add(MakeIntRow("最大幅 (px):",  p.VideoMaxWidth,  64, 2048, v => { p.VideoMaxWidth  = v; _save(); }));
        panel.Children.Add(MakeIntRow("最大高 (px):",  p.VideoMaxHeight, 64, 2048, v => { p.VideoMaxHeight = v; _save(); }));

        var avHeader = new TextBlock { Text = "動画・音声の再生音量", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(avHeader);
        panel.Children.Add(MakeIntRow("音量 (0〜100):", p.PlaybackVolume, 0, 100, v => { p.PlaybackVolume = v; _save(); }));

        var txtHeader = new TextBlock { Text = "テキスト", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(txtHeader);
        panel.Children.Add(MakeIntRow("最大幅 (px):",   p.TextMaxWidth,  64, 2048, v => { p.TextMaxWidth  = v; _save(); }));
        panel.Children.Add(MakeIntRow("最大高 (px):",   p.TextMaxHeight, 64, 2048, v => { p.TextMaxHeight = v; _save(); }));
        panel.Children.Add(MakeIntRow("読み込みサイズ (KiB):", p.TextReadKiB, 1, 1024, v => { p.TextReadKiB = v; _save(); }));

        item.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return item;
    }

    // ── 共通UIヘルパー ────────────────────────────────────────────
    private static UIElement MakeIntRow(string label, int initial, int min, int max, Action<int> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 220, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = initial.ToString(), Width = 70 };
        box.LostFocus += (_, _) =>
        {
            if (int.TryParse(box.Text, out int v))
            {
                v = Math.Max(min, Math.Min(max, v));
                box.Text = v.ToString();
                onChanged(v);
            }
            else
            {
                box.Text = initial.ToString();
            }
        };
        row.Children.Add(box);
        return row;
    }

    private static UIElement MakeExtensionRow(string label, System.Collections.Generic.List<string> initial,
        Action<System.Collections.Generic.List<string>> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = string.Join(", ", initial), Width = 360 };
        box.LostFocus += (_, _) =>
        {
            var list = box.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.StartsWith('.') ? s.ToLowerInvariant() : "." + s.ToLowerInvariant())
                .Distinct()
                .ToList();
            box.Text = string.Join(", ", list);
            onChanged(list);
        };
        row.Children.Add(box);
        return row;
    }

    private static bool IsValidHexColor(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('#') || s.Length != 7) return false;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }

    private void Reload()
    {
        _refresh();
        Close();
        var next = new SettingsWindow(_settings, _save, _refresh, _previewProvider);
        Reopened?.Invoke(next);
        next.Show();
    }

    /// <summary>
    /// Reload()によりこのウィンドウが閉じられ、新しいインスタンスに差し替わった際に発火する。
    /// App側で「現在開いている設定ウィンドウ」の参照を追従させ、二重起動防止を維持するために使う。
    /// </summary>
    public event Action<SettingsWindow>? Reopened;

    /// <summary>
    /// 自動起動の有効/無効をレジストリに反映し、トレイ右クリックメニュー側の
    /// チェック状態も同期させる（App.SyncAutoStartMenuState経由）。
    /// </summary>
    private static void SetStartup(bool enable, CheckBox source)
    {
        try
        {
            if (enable)
                QTBarExtension.Core.StartupRegistration.Enable();
            else
                QTBarExtension.Core.StartupRegistration.Disable();
        }
        catch (Exception ex)
        {
            // 失敗時はレジストリの実態に表示を合わせ直す
            source.IsChecked = QTBarExtension.Core.StartupRegistration.IsEnabled();
            MessageBox.Show($"自動起動設定の変更に失敗しました。\n{ex.Message}",
                "QTBarExtension", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // トレイメニューの「Windows起動時に自動実行」項目のチェックも連動させる
        QTBarExtension.App.SyncAutoStartMenuState();
    }
}
