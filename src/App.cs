using System;
using System.Collections.Generic;
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
        // フォーカス取得時に即座に位置補正（列ヘッダー隠れ対策）
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

        // サブフォルダメニューの開始
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
        // 子ウィンドウ化はSourceInitializedで行われる
    }

    private void OnMoved(ExplorerWindowInfo info)
    {
        if (!_bars.TryGetValue(info.MainHwnd, out var bar)) return;
        if (info.IsVisible)
        {
            bar.ShowBar();
            // リサイズ・移動のたびにShellViewを再押し込み（列ヘッダー対策）
            // PushShellViewDown内部の_isPushingフラグで二重実行は防がれる
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
            Icon    = System.Drawing.SystemIcons.Application,
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

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("終了");
        exitItem.Click += (_, _) => Dispatcher.InvokeAsync(Shutdown);

        menu.Items.Add(settingsItem);
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

    protected override void OnExit(ExitEventArgs e)
    {
        _previewService?.Dispose();
        _viewEnhancer?.Dispose();
        _subFolderMenuService?.Dispose();
        _trayIcon?.Dispose();
        _watcher?.Dispose();
        SaveSettings();
        base.OnExit(e);
    }
}
