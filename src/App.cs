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
        settingsItem.Click += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            var win = new SettingsWindow(_settings, SaveSettings,
                () => { foreach (var b in _bars.Values) { b.RebuildTabStrip(); } },
                _previewService?.Provider);
            win.Show();
        });

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
        _trayIcon.DoubleClick += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            var win = new SettingsWindow(_settings, SaveSettings,
                () => { foreach (var b in _bars.Values) { b.RebuildTabStrip(); } },
                _previewService?.Provider);
            win.Show();
        });
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
            string? exeDir = Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
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
            string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
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
            string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
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
