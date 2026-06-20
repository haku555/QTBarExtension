using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using QTBarExtension.Services;
using QTBarExtension.UI;

namespace QTBarExtension;

public partial class App : Application
{
    private ExplorerWatcher?                        _watcher;
    private readonly Dictionary<IntPtr, OverlayBar> _bars = [];
    private AppSettings                             _settings = new();
    private System.Windows.Forms.NotifyIcon?        _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _autoStartItem;
    private SettingsWindow?                          _settingsWindow;

    // フォルダビュー拡張（Shift+ホイール横スクロール / フルネームツールチップ）
    private ExplorerViewEnhancer? _viewEnhancer;
    // ファイルプレビュー（画像/動画/音声/テキスト）
    private PreviewHoverService?  _previewService;
    // サブフォルダメニュー
    private SubFolderMenuService? _subFolderMenuService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settings = SettingsStore.Load();
        SetupTrayIcon();

        _watcher = new ExplorerWatcher();
        _watcher.WindowAppeared += info => Dispatcher.InvokeAsync(() => EnsureBar(info));
        _watcher.WindowMoved    += info => Dispatcher.InvokeAsync(() => OnMoved(info));
        _watcher.WindowClosed   += hwnd => Dispatcher.InvokeAsync(() => OnClosed(hwnd));
        _watcher.WindowFocused  += info => Dispatcher.InvokeAsync(() =>
        {
            if (_bars.TryGetValue(info.MainHwnd, out var bar))
                bar.PositionToExplorer();
        });

        foreach (var (_, info) in _watcher.Windows)
            Dispatcher.InvokeAsync(() => EnsureBar(info));

        // フォルダビュー拡張機能の開始
        _viewEnhancer = new ExplorerViewEnhancer(_settings);
        _viewEnhancer.Start();

        // プレビュー機能の開始
        _previewService = new PreviewHoverService(_settings);
        _previewService.Start();

        // サブフォルダメニュー機能の開始
        _subFolderMenuService = new SubFolderMenuService(_settings, _previewService.Provider);
        _subFolderMenuService.Start();
    }

    private void EnsureBar(ExplorerWindowInfo info)
    {
        if (_bars.ContainsKey(info.MainHwnd)) return;
        var bar = new OverlayBar(info, _settings, SaveSettings, _ => { });
        _bars[info.MainHwnd] = bar;
        if (_subFolderMenuService != null)
            bar.SetSubFolderMenuService(_subFolderMenuService);
        bar.Show();
        bar.RefreshTabBarVisibility();
    }

    private void OnMoved(ExplorerWindowInfo info)
    {
        if (!_bars.TryGetValue(info.MainHwnd, out var bar)) return;
        if (info.IsVisible)
        {
            bar.ShowBar();
            bar.RepushShellView();
        }
        else
        {
            bar.HideBar();
        }
    }

    private void OnClosed(IntPtr hwnd)
    {
        if (!_bars.TryGetValue(hwnd, out var bar)) return;
        _bars.Remove(hwnd);
        bar.Close();
    }

    private void SaveSettings() => SettingsStore.Save(_settings);

    /// <summary>
    /// 設定ウィンドウを表示する。既に開いている場合は新規生成せず、
    /// 既存のウィンドウを前面に出してアクティブ化するだけにする（二重起動防止）。
    /// </summary>
    private void ShowSettingsWindow()
    {
        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        TrackSettingsWindow(new SettingsWindow(_settings, SaveSettings,
            () => { foreach (var b in _bars.Values) { b.RebuildTabStrip(); } },
            _previewService?.Provider));
    }

    /// <summary>
    /// 設定ウィンドウのインスタンスを追跡対象として登録する。
    /// Reload()による内部的な作り直し（Reopenedイベント）にも追従する。
    /// </summary>
    private void TrackSettingsWindow(SettingsWindow win)
    {
        _settingsWindow = win;
        win.Closed   += (_, _) => { if (_settingsWindow == win) _settingsWindow = null; };
        win.Reopened += next => TrackSettingsWindow(next);
        win.Show();
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "QTBarExtension",
            Visible = true,
            Icon    = LoadAppIcon(),
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("設定");
        settingsItem.Click += (_, _) => Dispatcher.InvokeAsync(ShowSettingsWindow);

        var shortcutItem = new System.Windows.Forms.ToolStripMenuItem("デスクトップにショートカットを作成");
        shortcutItem.Click += (_, _) => CreateDesktopShortcut();

        _autoStartItem = new System.Windows.Forms.ToolStripMenuItem("Windows起動時に自動実行")
        {
            CheckOnClick = true,
            Checked      = StartupRegistration.IsEnabled(),
        };
        _autoStartItem.Click += (_, _) =>
        {
            try
            {
                if (_autoStartItem.Checked)
                    StartupRegistration.Enable();
                else
                    StartupRegistration.Disable();
            }
            catch (Exception ex)
            {
                _autoStartItem.Checked = StartupRegistration.IsEnabled();
                System.Windows.Forms.MessageBox.Show(
                    $"自動実行設定の変更に失敗しました。\n{ex.Message}",
                    "QTBarExtension",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        };

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("終了");
        exitItem.Click += (_, _) => Dispatcher.InvokeAsync(Shutdown);

        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(shortcutItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip  = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.InvokeAsync(ShowSettingsWindow);
    }

    /// <summary>
    /// 設定ウィンドウ（一般タブ）の「Windowsスタートアップ時に自動起動」チェックボックスから
    /// 呼び出され、トレイ右クリックメニュー側の「Windows起動時に自動実行」のチェック状態を
    /// 現在のレジストリ実態（StartupRegistration.IsEnabled）に同期させる。
    /// 設定側→トレイ側の一方向だが、トレイ側→設定側は設定ウィンドウを開き直す際に
    /// IsEnabled()を再読込することで自然に反映される。
    /// </summary>
    public static void SyncAutoStartMenuState()
    {
        if (Current is App app && app._autoStartItem != null)
        {
            app._autoStartItem.Checked = StartupRegistration.IsEnabled();
        }
    }

    /// <summary>
    /// 設定ウィンドウ（一般タブ）の「タブバーを表示する」チェックボックスから呼び出され、
    /// 現在存在する全Explorerウィンドウのタブバーに対して表示/非表示を即座に反映する。
    /// AppSettings.ShowTabBarは呼び出し側で既に更新済みである前提。
    /// </summary>
    public static void ApplyTabBarVisibility()
    {
        if (Current is App app)
        {
            foreach (var bar in app._bars.Values)
                bar.RefreshTabBarVisibility();
        }
    }

    /// <summary>
    /// アプリ/トレイアイコンを読み込む。
    /// 1) exe隣の icon/QTBarExtension.ico
    /// 2) アセンブリ埋め込みリソース
    /// 3) 実行ファイル自身のアイコン（ApplicationIconで埋め込み済み）
    /// 4) 最終フォールバックとしてシステムアイコン
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // Environment.ProcessPath は PublishSingleFile 時でも .exe 本体のパスを返す
            string? exeDir = Path.GetDirectoryName(
                Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
            if (exeDir != null)
            {
                string iconPath = Path.Combine(exeDir, "icon", "QTBarExtension.ico");
                if (File.Exists(iconPath))
                    return new System.Drawing.Icon(iconPath);
            }
        }
        catch { }

        try
        {
            var uri = new Uri("pack://application:,,,/QTBarExtension;component/icon/QTBarExtension.ico",
                               UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo != null)
                return new System.Drawing.Icon(streamInfo.Stream);
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch { }

        return System.Drawing.SystemIcons.Application;
    }

    private void CreateDesktopShortcut()
    {
        try
        {
            string? exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;

            string desktop      = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string shortcutPath = Path.Combine(desktop, "QTBarExtension.lnk");

            ShortcutHelper.CreateShortcut(
                shortcutPath: shortcutPath,
                targetPath:   exePath,
                workingDir:   Path.GetDirectoryName(exePath) ?? "",
                iconPath:     Path.Combine(Path.GetDirectoryName(exePath) ?? "", "icon", "QTBarExtension.ico"),
                description:  "QTBarExtension - Explorerタブ拡張");

            _trayIcon?.ShowBalloonTip(3000, "QTBarExtension",
                "デスクトップにショートカットを作成しました。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(
                $"ショートカットの作成に失敗しました。\n{ex.Message}",
                "QTBarExtension",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _subFolderMenuService?.Dispose();
        _previewService?.Dispose();
        _viewEnhancer?.Dispose();
        _trayIcon?.Dispose();
        _watcher?.Dispose();
        SaveSettings();
        base.OnExit(e);
    }
}
