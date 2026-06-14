using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using QTBarExtension.UI;
using static QTBarExtension.Core.NativeMethods;
using static QTBarExtension.Core.NativeMethodsExtra;

namespace QTBarExtension.Services;

/// <summary>
/// Explorerのフォルダビュー上でのファイルホバーを検出し、
/// PreviewPopupWindowでファイルプレビューを表示するサービス。
///
/// 動作フロー:
///   1. WH_MOUSE_LL でマウス座標を常時更新
///   2. 50ms ポーリングでマウスが止まったかカウント
///   3. HoverDelayMs 分静止したら UIA でファイル名取得
///   4. 画像: ローディングスキップ → バックグラウンドで読み込み → 直接表示
///      動画/音声/テキスト: Now Loading 即時表示 → バックグラウンド読み込み → 置換
///   5. 表示中にマウスが 40px 以上移動したら非表示
/// </summary>
public class PreviewHoverService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PreviewContentProvider _provider;
    private readonly Dispatcher _dispatcher;

    private NativeMethods.POINT _lastPt;
    private NativeMethods.POINT _stablePt;
    private NativeMethods.POINT _lastShownPt;
    private NativeMethods.POINT _anchorPt; // ポップアップ表示位置の基準点（ナビゲーション中も固定）
    private int _stableCount;

    private readonly DispatcherTimer _pollTimer;
    private bool _uiaRunning;

    private string? _lastHoveredPath;
    private string? _loadingPath;
    private string? _shownPath;

    private PreviewPopupWindow? _popup;

    private IntPtr _lastCabinetHwnd = IntPtr.Zero;
    private string? _lastFolderPath;

    // 現在表示中のフォルダ/zipナビゲーション状態（キーボードフックから参照）
    private List<string>? _currentFolderItems;
    private int _currentFolderIndex;

    private LowLevelMouseProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;

    private LowLevelKeyboardProc? _kbProc;
    private IntPtr _kbHookId = IntPtr.Zero;

    private int StableThreshold => Math.Max(1, _settings.Preview.HoverDelayMs / 50);

    public PreviewHoverService(AppSettings settings)
    {
        _settings = settings;
        _provider = new PreviewContentProvider(settings.Preview);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _pollTimer.Tick += OnPollTick;
    }

    public PreviewContentProvider Provider => _provider;

    public void Start()
    {
        if (_hookId == IntPtr.Zero)
        {
            _proc = HookCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod  = proc.MainModule!;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
                GetModuleHandle(mod.ModuleName!), 0);
        }
        if (_kbHookId == IntPtr.Zero)
        {
            _kbProc = KeyboardHookCallback;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var mod  = proc.MainModule!;
            _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc,
                GetModuleHandle(mod.ModuleName!), 0);
        }
        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        HidePopup();
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        if (_kbHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbHookId);
            _kbHookId = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _lastPt = data.pt;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
        {
            // ポップアップが表示中かつフォルダ/zipナビゲーション可能な場合のみ
            // 左右キーを横取りしてプレビューのファイルを切り替える。
            // それ以外（通常時）はExplorerの操作に渡す。
            if (_popup != null && _popup.IsVisible &&
                _currentFolderItems != null && _currentFolderItems.Count > 1)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == VK_LEFT || data.vkCode == VK_RIGHT)
                {
                    int delta = data.vkCode == VK_LEFT ? -1 : +1;
                    _dispatcher.InvokeAsync(() =>
                    {
                        if (_currentFolderItems == null || _currentFolderItems.Count == 0) return;
                        int next = (_currentFolderIndex + delta + _currentFolderItems.Count)
                                   % _currentFolderItems.Count;
                        NavigateFolderItem(_currentFolderItems, next);
                    });
                    return new IntPtr(1); // イベントを消費（Explorer側の選択移動を抑制）
                }
            }
        }
        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
    }

    // ── 50ms ポーリング ───────────────────────────────────────────────
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!_settings.Preview.Enabled) { HidePopup(); return; }
        if (_uiaRunning) return;

        var pt = _lastPt;

        // マウスが動いた
        if (pt.X != _stablePt.X || pt.Y != _stablePt.Y)
        {
            _stablePt    = pt;
            _stableCount = 0;

            // 表示中・ロード中でも 40px 以内の移動なら消さない
            if (_shownPath != null || _loadingPath != null)
            {
                if (IsPointInsidePopup(pt))
                    return;

                int dx = pt.X - _lastShownPt.X;
                int dy = pt.Y - _lastShownPt.Y;
                if (dx * dx + dy * dy > 40 * 40)
                    HidePopup();
            }
            return;
        }

        _stableCount++;
        if (_stableCount != StableThreshold) return;

        // ポップアップ上にいる場合は再トリガーしない
        if (IsPointInsidePopup(pt)) return;

        // 非アクティブ抑制
        if (!_settings.Preview.ShowWhenInactive)
        {
            IntPtr fg        = GetForegroundWindow();
            IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(pt);
            if (!IsSameTopLevel(hwndUnder, fg)) return;
        }

        IntPtr cabinet = FindCabinetAtPoint(pt);
        if (cabinet == IntPtr.Zero) return;

        if (cabinet != _lastCabinetHwnd)
        {
            _lastCabinetHwnd = cabinet;
            _lastFolderPath  = GetFolderPathForCabinet(cabinet);
        }
        if (string.IsNullOrEmpty(_lastFolderPath)) return;

        var folderPath = _lastFolderPath;
        _uiaRunning = true;

        Task.Run(() =>
        {
            string? itemName = null;
            try { itemName = GetAutomationItemNameAt(pt); }
            catch { }
            finally
            {
                _dispatcher.InvokeAsync(() =>
                {
                    _uiaRunning = false;
                    OnItemNameResolved(itemName, folderPath);
                });
            }
        });
    }

    // ── UIA 結果処理 ─────────────────────────────────────────────────
    private void OnItemNameResolved(string? itemName, string folderPath)
    {
        if (string.IsNullOrEmpty(itemName))
        {
            _lastHoveredPath = null;
            return;
        }

        // UIAは拡張子なしのファイル名を返すことがあるため、実ファイルを探す
        string fullPath = ResolveItemPath(folderPath, itemName);
        if (fullPath == _lastHoveredPath) return;
        _lastHoveredPath = fullPath;

        // ── フォルダ/zip のプレビュー ──────────────────────────────
        // 通常フォルダ
        if (PreviewContentProvider.IsDirectory(fullPath))
        {
            if (_settings.Preview.PreviewFolderContents)
                ShowFolderPreview(fullPath);
            return;
        }
        // zip以外も含む圧縮フォルダ自体
        if (_provider.IsArchiveFile(fullPath))
        {
            if (_settings.Preview.PreviewArchiveContents)
                ShowArchivePreview(fullPath);
            return;
        }

        // ── 通常ファイルのプレビュー ──────────────────────────────
        var kind = _provider.GetKind(fullPath);
        if (kind == PreviewKind.None || kind == PreviewKind.Unsupported) return;

        if (kind == PreviewKind.Image)
            LoadAndShowDirect(fullPath);
        else
            ShowLoadingThenPreview(fullPath);
    }

    /// <summary>
    /// UIAから得たアイテム名（拡張子なしの場合あり）からフルパスを解決する。
    /// 拡張子ありのパスが存在すればそれを、なければフォルダ内を検索する。
    /// </summary>
    private static string ResolveItemPath(string folderPath, string itemName)
    {
        // まず名前をそのままつなげてみる（フォルダ名 or 拡張子付きファイル名）
        string direct = Path.Combine(folderPath, itemName);
        if (Directory.Exists(direct) || File.Exists(direct))
            return direct;

        // 拡張子なしの場合: フォルダ内で先頭一致するファイルを探す
        try
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(itemName);
            // itemNameと完全一致（拡張子なし）するか確認してから検索
            if (!string.Equals(itemName, nameNoExt, StringComparison.Ordinal))
                return direct; // 拡張子があるのに見つからない→そのまま返す

            var candidates = Directory.GetFiles(folderPath, itemName + ".*");
            if (candidates.Length > 0)
                return candidates[0];
        }
        catch { }

        return direct;
    }

    // ── フォルダプレビュー ──────────────────────────────────────────
    private void ShowFolderPreview(string folderPath)
    {
        var items = _provider.GetFolderPreviewItems(folderPath);
        if (items.Count == 0) return;

        string firstItem = items[0];
        string ext = Path.GetExtension(firstItem).ToLowerInvariant();
        bool isImage = _settings.Preview.ImageExtensions.Contains(ext);

        if (isImage)
            LoadAndShowDirect(firstItem, items, 0);
        else
            ShowLoadingThenPreview(firstItem, items, 0);
    }

    private void ShowArchivePreview(string archivePath)
    {
        var items = _provider.GetArchivePreviewItems(archivePath);
        if (items.Count == 0) return;

        string firstItem = items[0];
        string ext = Path.GetExtension(firstItem).ToLowerInvariant();
        bool isImage = _settings.Preview.ImageExtensions.Contains(ext);

        if (isImage)
            LoadAndShowDirect(firstItem, items, 0);
        else
            ShowLoadingThenPreview(firstItem, items, 0);
    }

    // ── ロード＆表示 ────────────────────────────────────────────────
    private void LoadAndShowDirect(string path,
        List<string>? folderItems = null, int folderIndex = 0, bool isNavigation = false)
    {
        _loadingPath = path;
        _shownPath   = null;
        _lastShownPt = _lastPt;
        if (!isNavigation) _anchorPt = _lastPt;
        var placeAt = _anchorPt;

        Task.Run(() =>
        {
            PreviewInfo? info = null;
            try { info = _provider.Load(path, folderItems, folderIndex); }
            catch { }

            _dispatcher.InvokeAsync(() =>
            {
                if (_loadingPath != path) return;
                _loadingPath = null;

                if (info == null) { HidePopup(); return; }

                EnsurePopup();
                _popup!.ShowPreview(info, placeAt.X + 20, placeAt.Y + 20);
                _shownPath = path;
                _currentFolderItems = folderItems;
                _currentFolderIndex = folderIndex;
            });
        });
    }

    private void ShowLoadingThenPreview(string path,
        List<string>? folderItems = null, int folderIndex = 0, bool isNavigation = false)
    {
        EnsurePopup();

        if (!isNavigation) _anchorPt = _lastPt;
        var placeAt = _anchorPt;

        _popup!.ShowLoading(path, placeAt.X + 20, placeAt.Y + 20);
        _loadingPath = path;
        _shownPath   = null;
        _lastShownPt = _lastPt;

        Task.Run(() =>
        {
            PreviewInfo? info = null;
            try { info = _provider.Load(path, folderItems, folderIndex); }
            catch { }

            _dispatcher.InvokeAsync(() =>
            {
                if (_loadingPath != path) return;
                _loadingPath = null;

                if (info == null) { HidePopup(); return; }

                EnsurePopup();
                _popup!.ShowPreview(info, placeAt.X + 20, placeAt.Y + 20);
                _shownPath = path;
                _currentFolderItems = folderItems;
                _currentFolderIndex = folderIndex;
            });
        });
    }

    // ── ナビゲーション要求（PreviewPopupWindowから呼ばれる） ──────────
    public void NavigateFolderItem(List<string> items, int newIndex)
    {
        if (newIndex < 0 || newIndex >= items.Count) return;
        string path = items[newIndex];
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isImage = _settings.Preview.ImageExtensions.Contains(ext);
        bool isVideo = _settings.Preview.VideoExtensions.Contains(ext);
        if (!isImage && !isVideo) return;

        _lastHoveredPath = path;
        _currentFolderItems = items;
        _currentFolderIndex = newIndex;

        if (isImage)
            LoadAndShowDirect(path, items, newIndex, isNavigation: true);
        else
            ShowLoadingThenPreview(path, items, newIndex, isNavigation: true);
    }

    private void EnsurePopup()
    {
        if (_popup == null)
        {
            _popup = new PreviewPopupWindow(_settings.Preview);
            _popup.OnNavigateRequest = (items, idx) => NavigateFolderItem(items, idx);
        }
    }

    private void HidePopup()
    {
        _loadingPath = null;
        _shownPath   = null;
        _currentFolderItems = null;
        if (_popup != null && _popup.IsVisible)
            _popup.Hide();
    }

    private bool IsPointInsidePopup(NativeMethods.POINT pt)
    {
        if (_popup == null || !_popup.IsVisible) return false;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_popup).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!GetWindowRect(hwnd, out var rect)) return false;
            return pt.X >= rect.Left - 8 && pt.X <= rect.Right  + 8 &&
                   pt.Y >= rect.Top  - 8 && pt.Y <= rect.Bottom + 8;
        }
        catch { return false; }
    }

    // ── UIAutomation ─────────────────────────────────────────────────
    private static string? GetAutomationItemNameAt(NativeMethods.POINT pt)
    {
        var screenPt = new System.Windows.Point(pt.X, pt.Y);
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(screenPt); }
        catch { return null; }
        if (element == null) return null;

        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cur = element;
        for (int depth = 0; depth < 6 && cur != null; depth++)
        {
            ControlType? ct;
            try
            {
                ct = cur.GetCurrentPropertyValue(
                    AutomationElementIdentifiers.ControlTypeProperty) as ControlType;
            }
            catch { break; }

            if (ct == ControlType.ListItem ||
                ct == ControlType.DataItem ||
                ct == ControlType.TreeItem)
            {
                string? name;
                try
                {
                    name = cur.GetCurrentPropertyValue(
                        AutomationElementIdentifiers.NameProperty) as string;
                }
                catch { name = null; }

                if (!string.IsNullOrEmpty(name))
                    return SanitizeItemName(name);
            }

            if (ct == ControlType.List   || ct == ControlType.Group ||
                ct == ControlType.Pane   || ct == ControlType.Window)
                break;

            try { cur = walker.GetParent(cur); }
            catch { break; }
        }
        return null;
    }

    private static string SanitizeItemName(string raw)
    {
        // Explorerが詳細表示時に "ファイル名\nサイズ\n日付" のような複数行を返す場合がある
        int nl = raw.IndexOfAny(['\r', '\n', '\t']);
        if (nl > 0) raw = raw[..nl];
        return raw.Trim();
    }

    private static IntPtr FindCabinetAtPoint(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethodsExtra.WindowFromPoint(pt);
        var cur = hwnd;
        for (int i = 0; i < 12 && cur != IntPtr.Zero; i++)
        {
            if (GetClassNameStr(cur) == "CabinetWClass") return cur;
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return IntPtr.Zero;
    }

    private static string? GetFolderPathForCabinet(IntPtr cabinet)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell   = System.Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? win = windows.Item(i);
                    if (win == null) continue;
                    if ((IntPtr)(int)win.HWND == cabinet)
                        return ShellHelper.UrlToLocalPath((string?)win.LocationURL ?? "");
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool IsSameTopLevel(IntPtr hwnd, IntPtr fg)
    {
        var cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if (cur == fg) return true;
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return false;
    }

    private static string GetClassNameStr(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(64);
        NativeMethodsExtra.GetClassName(hwnd, sb, 64);
        return sb.ToString();
    }
}
