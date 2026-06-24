using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
/// シンプル設計:
///   - WH_MOUSE_LL でカーソル座標のみ追跡
///   - DispatcherTimer (16ms) で静止を検知 → UIA でアイテム名取得 → ロード開始
///   - 座標→パスのキャッシュは持たない（誤ヒットの原因になるため）
///   - カーソルが現在アイテム矩形の外に出たら即非表示
///   - CancellationToken で古いロードを即キャンセル
/// </summary>
public class PreviewHoverService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PreviewContentProvider _provider;
    private readonly Dispatcher _dispatcher;

    // マウス追跡 (volatile: フックスレッド↔UIスレッド)
    private volatile int _lastPtX, _lastPtY;
    private int _stablePtX, _stablePtY;
    private int _stableCount;

    private readonly DispatcherTimer _pollTimer;
    private bool _uiaRunning;

    // ── ロード状態管理 ──────────────────────────────────────────────
    private int _loadSerial;
    private string? _currentLoadPath; // 現在ロード中or表示中のパス
    private bool _currentIsImage;     // 現在表示中が画像プレビューかどうか
    private bool _currentIsText;      // 現在表示中がテキストプレビューかどうか
    private bool _currentIsAnimatedWebp; // 現在表示中がアニメーションWebPかどうか
    private bool _currentIsFolderPreview; // フォルダ/圧縮フォルダのプレビューとして開始されたか
    private CancellationTokenSource? _loadCts;
    private PreviewPopupWindow? _popup;

    // 現在表示中アイテムの矩形（物理ピクセル）。カーソル離脱検知に使う
    private System.Windows.Rect _currentItemBounds;

    // フォルダ追跡
    private IntPtr _lastCabinetHwnd = IntPtr.Zero;
    private string? _lastFolderPath;

    // UIAキャッシュ（直前クエリ、同座標での二重UIA呼び出しを防ぐ）
    private int _lastUiaPtX = -9999, _lastUiaPtY = -9999;
    private string? _lastUiaName;
    private System.Windows.Rect _lastUiaBounds;

    // フォルダナビゲーション
    private List<string>? _currentFolderItems;
    private int _currentFolderIndex;

    // フック
    private LowLevelMouseProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _kbProc;
    private IntPtr _kbHookId = IntPtr.Zero;

    private const int PollMs = 16;
    private int StableThreshold => Math.Max(1, _settings.Preview.HoverDelayMs / PollMs);

    // アイテム矩形の外と判定するマージン(px)
    private const int LeaveMargin = 12;

    public PreviewHoverService(AppSettings settings)
    {
        _settings = settings;
        _provider = new PreviewContentProvider(settings.Preview);
        _dispatcher = Dispatcher.CurrentDispatcher;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs)
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
        HidePopupImmediate();
        if (_hookId   != IntPtr.Zero) { UnhookWindowsHookEx(_hookId);   _hookId   = IntPtr.Zero; }
        if (_kbHookId != IntPtr.Zero) { UnhookWindowsHookEx(_kbHookId); _kbHookId = IntPtr.Zero; }
    }

    public void Dispose() => Stop();

    // ── マウスフック（座標追跡のみ） ──────────────────────────────────
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            _lastPtX = data.pt.X;
            _lastPtY = data.pt.Y;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ── キーボードフック（フォルダ内ナビゲーション） ─────────────────
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
        {
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
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
    }

    // ── ポーリング（静止検知 → UIA → ロード） ──────────────────────
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (!_settings.Preview.Enabled) { HidePopupImmediate(); return; }

        int ptX = _lastPtX, ptY = _lastPtY;

        // ── カーソル移動中の離脱検知 ─────────────────────────────────
        // プレビュー表示中にカーソルがアイテム矩形外へ出たら非表示
        if (_currentLoadPath != null && !_currentItemBounds.IsEmpty)
        {
            bool inside = ptX >= _currentItemBounds.Left   - LeaveMargin &&
                          ptX <= _currentItemBounds.Right  + LeaveMargin &&
                          ptY >= _currentItemBounds.Top    - LeaveMargin &&
                          ptY <= _currentItemBounds.Bottom + LeaveMargin;

            var popupPt = new NativeMethods.POINT { X = ptX, Y = ptY };
            bool onPopup = IsPointInsidePopup(popupPt);

            if (!inside && !onPopup)
            {
                HidePopupImmediate();
                return;
            }

            // HidePreviewOnHover 有効 かつ 画像プレビュー表示中（フォルダ/圧縮フォルダ由来は除外）のみ:
            // ポップアップ上にマウスが来たらポップアップを閉じ、その下のアイテムを再検出する。
            if (onPopup && _currentIsImage && !_currentIsAnimatedWebp && !_currentIsFolderPreview && _settings.Preview.HidePreviewOnHover)
            {
                HidePopupImmediate();
                return;
            }

            // HideAnimatedWebpOnHover 有効 かつ アニメーションWebPプレビュー表示中:
            if (onPopup && _currentIsAnimatedWebp && !_currentIsFolderPreview && _settings.Preview.HideAnimatedWebpOnHover)
            {
                HidePopupImmediate();
                return;
            }

            // HideTextPreviewOnHover 有効 かつ テキストプレビュー表示中:
            // ポップアップ上にマウスが来たらポップアップを閉じ、その下のアイテムを再検出する。
            if (onPopup && _currentIsText && !_currentIsFolderPreview && _settings.Preview.HideTextPreviewOnHover)
            {
                HidePopupImmediate();
                return;
            }
        }

        // ── 静止待ち ──────────────────────────────────────────────────
        if (ptX != _stablePtX || ptY != _stablePtY)
        {
            _stablePtX   = ptX;
            _stablePtY   = ptY;
            _stableCount = 0;
            return;
        }

        _stableCount++;
        if (_stableCount < StableThreshold) return;
        if (_stableCount > StableThreshold) return; // 1回だけトリガー

        if (_uiaRunning) return;

        var pt = new NativeMethods.POINT { X = ptX, Y = ptY };

        // ポップアップ上ならスキップ（HidePreviewOnHover=false の場合のみここに到達）
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

        // デスクトップアイコン（Progman/WorkerW）の場合、設定で無効なら何もしない
        {
            string cls = GetClassNameStr(cabinet);
            if ((cls == "Progman" || cls == "WorkerW") && !_settings.Preview.PreviewDesktopIcons) return;
        }

        // フォルダが変わった → UIAキャッシュをクリア
        string? newFolderPath = GetFolderPathForCabinet(cabinet);
        if (string.IsNullOrEmpty(newFolderPath)) return;

        if (newFolderPath != _lastFolderPath || cabinet != _lastCabinetHwnd)
        {
            _lastUiaName = null;
            _lastUiaPtX  = -9999;
            _lastUiaPtY  = -9999;
        }

        _lastCabinetHwnd = cabinet;
        _lastFolderPath  = newFolderPath;

        var folderPath  = newFolderPath;
        int capturedPtX = ptX, capturedPtY = ptY;

        // UIAキャッシュヒット (±8px、同座標での再問い合わせを防ぐ)
        if (Math.Abs(capturedPtX - _lastUiaPtX) <= 8 &&
            Math.Abs(capturedPtY - _lastUiaPtY) <= 8 && _lastUiaName != null)
        {
            OnItemNameResolved(_lastUiaName, folderPath, false, _lastUiaBounds);
            return;
        }

        _uiaRunning = true;

        Task.Factory.StartNew(() =>
        {
            string? itemName = null;
            bool isDetailView = false;
            System.Windows.Rect itemBounds = System.Windows.Rect.Empty;
            var capturedPt = new NativeMethods.POINT { X = capturedPtX, Y = capturedPtY };
            try
            {
                itemName     = GetAutomationItemNameAt(capturedPt, out itemBounds);
                isDetailView = DetectDetailViewAt(capturedPt);
            }
            catch { }

            _dispatcher.InvokeAsync(() =>
            {
                _uiaRunning    = false;
                _lastUiaPtX   = capturedPtX;
                _lastUiaPtY   = capturedPtY;
                _lastUiaName  = itemName;
                _lastUiaBounds = itemBounds;
                OnItemNameResolved(itemName, folderPath, isDetailView, itemBounds);
            });
        }, TaskCreationOptions.PreferFairness);
    }

    // ── UIA結果処理 ──────────────────────────────────────────────────
    private void OnItemNameResolved(string? itemName, string folderPath, bool isDetailView,
        System.Windows.Rect itemBounds)
    {
        if (string.IsNullOrEmpty(itemName)) return;

        string fullPath = ResolveItemPath(folderPath, itemName);
        if (fullPath == _currentLoadPath) return;

        // 現在アイテム矩形を更新（離脱検知用）
        // UIAのBoundingRectangleはDIP→物理ピクセルへ変換
        if (!itemBounds.IsEmpty && itemBounds.Width > 0)
        {
            double scale = GetDipScaleForCurrentMonitor((int)itemBounds.Left, (int)itemBounds.Top);
            _currentItemBounds = ScaleRect(itemBounds, scale);
        }
        else
        {
            _currentItemBounds = System.Windows.Rect.Empty;
        }

        // アンカー（ポップアップ表示位置）= アイテム矩形の右下（物理ピクセル）
        NativeMethods.POINT anchor;
        if (!_currentItemBounds.IsEmpty)
            anchor = new NativeMethods.POINT { X = (int)_currentItemBounds.Right, Y = (int)_currentItemBounds.Bottom };
        else
            anchor = new NativeMethods.POINT { X = _lastPtX, Y = _lastPtY };

        // フォルダプレビュー
        if (PreviewContentProvider.IsDirectory(fullPath))
        {
            if (_settings.Preview.PreviewFolderContents)
            {
                if (_settings.Preview.NoFolderPreviewInDetailView && isDetailView) return;
                ShowFolderPreview(fullPath, anchor);
            }
            return;
        }
        // アーカイブプレビュー
        if (_provider.IsArchiveFile(fullPath))
        {
            if (_settings.Preview.PreviewArchiveContents)
            {
                if (_settings.Preview.NoFolderPreviewInDetailView && isDetailView) return;
                ShowArchivePreview(fullPath, anchor);
            }
            return;
        }

        var kind = _provider.GetKind(fullPath);
        if (kind == PreviewKind.None || kind == PreviewKind.Unsupported) return;

        StartLoad(fullPath, anchor);
    }

    private static string ResolveItemPath(string folderPath, string itemName)
    {
        string direct = Path.Combine(folderPath, itemName);
        if (Directory.Exists(direct) || File.Exists(direct)) return direct;
        try
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(itemName);
            if (!string.Equals(itemName, nameNoExt, StringComparison.Ordinal)) return direct;
            var candidates = Directory.GetFiles(folderPath, itemName + ".*");
            if (candidates.Length > 0) return candidates[0];
        }
        catch { }
        return direct;
    }

    // ── フォルダ/アーカイブプレビュー ────────────────────────────────
    private void ShowFolderPreview(string folderPath, NativeMethods.POINT anchor)
    {
        _currentLoadPath = folderPath; // 同フォルダ上でカーソルが動いても再ロードしない
        _currentIsFolderPreview = true;

        var items = _provider.GetFolderPreviewItems(folderPath);
        if (items.Count == 0) return;
        StartLoad(items[0], anchor, items, 0, isNavigation: true);
    }

    private void ShowArchivePreview(string archivePath, NativeMethods.POINT anchor)
    {
        _currentLoadPath = archivePath; // 同アーカイブ上でカーソルが動いても再ロードしない
        _currentIsFolderPreview = true;

        var items = _provider.GetArchivePreviewItems(archivePath);
        if (items.Count == 0) return;
        StartLoad(items[0], anchor, items, 0, isNavigation: true);
    }

    // ── 統合ロード開始 ───────────────────────────────────────────────
    private void StartLoad(string path, NativeMethods.POINT anchor,
        List<string>? folderItems = null, int folderIndex = 0, bool isNavigation = false)
    {
        if (path == _currentLoadPath && !isNavigation) return;

        // 旧ロードをキャンセル
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var cts    = _loadCts;
        var serial = ++_loadSerial;

        _currentLoadPath = path;
        var placeAt      = anchor;

        var kind    = _provider.GetKind(path);
        bool isImage = kind == PreviewKind.Image;
        bool isText  = kind == PreviewKind.Text;
        _currentIsImage = isImage;
        _currentIsText  = isText;
        _currentIsAnimatedWebp = false; // Load()完了後に更新される
        // フォルダ/アーカイブ由来でない直接ファイルホバーの場合はフラグをリセット
        if (!isNavigation) _currentIsFolderPreview = false;

        // テキストプレビュー無効の場合はスキップ
        if (isText && !_settings.Preview.PreviewTextFiles) return;

        // 画像以外はLoading表示
        if (!isImage)
        {
            EnsurePopup();
            _popup!.ShowLoading(path, placeAt.X, placeAt.Y);
        }

        Task.Run(() =>
        {
            PreviewInfo? info = null;
            try
            {
                if (!cts.IsCancellationRequested)
                    info = _provider.Load(path, folderItems, folderIndex);
            }
            catch { }

            _dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (_loadSerial != serial) return;

                if (info == null) { HidePopupImmediate(); return; }

                EnsurePopup();
                _popup!.ShowPreview(info, placeAt.X, placeAt.Y);
                _currentIsAnimatedWebp = info.IsAnimatedWebp;
                _currentFolderItems = folderItems;
                _currentFolderIndex = folderIndex;
            });
        }, cts.Token);
    }

    // ── ナビゲーション ────────────────────────────────────────────────
    public void NavigateFolderItem(List<string> items, int newIndex)
    {
        if (newIndex < 0 || newIndex >= items.Count) return;
        string path = items[newIndex];
        string ext  = Path.GetExtension(path).ToLowerInvariant();
        bool isImage = _settings.Preview.ImageExtensions.Contains(ext);
        bool isVideo = _settings.Preview.VideoExtensions.Contains(ext);
        if (!isImage && !isVideo) return;

        _currentFolderItems = items;
        _currentFolderIndex = newIndex;

        // ナビゲーション時はアンカーを維持（ポップアップ位置を動かさない）
        var anchor = _currentItemBounds.IsEmpty
            ? new NativeMethods.POINT { X = _lastPtX, Y = _lastPtY }
            : new NativeMethods.POINT { X = (int)_currentItemBounds.Right, Y = (int)_currentItemBounds.Bottom };

        StartLoad(path, anchor, items, newIndex, isNavigation: true);
    }

    private void EnsurePopup()
    {
        if (_popup == null)
        {
            _popup = new PreviewPopupWindow(_settings.Preview);
            _popup.OnNavigateRequest = (items, idx) => NavigateFolderItem(items, idx);
        }
    }

    // ── 即時非表示 ───────────────────────────────────────────────────
    private void HidePopupImmediate()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        _loadSerial++;

        _currentLoadPath      = null;
        _currentIsImage       = false;
        _currentIsText        = false;
        _currentIsAnimatedWebp = false;
        _currentIsFolderPreview = false;
        _currentItemBounds    = System.Windows.Rect.Empty;
        _currentFolderItems = null;

        // UIAキャッシュ無効化
        _lastUiaName = null;
        _lastUiaPtX  = -9999;
        _lastUiaPtY  = -9999;

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
            const int margin = 12;
            return pt.X >= rect.Left  - margin && pt.X <= rect.Right  + margin &&
                   pt.Y >= rect.Top   - margin && pt.Y <= rect.Bottom + margin;
        }
        catch { return false; }
    }

    // ── UIAutomation ─────────────────────────────────────────────────
    private static string? GetAutomationItemNameAt(NativeMethods.POINT pt,
        out System.Windows.Rect itemBounds)
    {
        itemBounds = System.Windows.Rect.Empty;
        AutomationElement? element;
        try { element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); }
        catch { return null; }
        if (element == null) return null;

        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cur = element;
        for (int depth = 0; depth < 6 && cur != null; depth++)
        {
            ControlType? ct;
            try { ct = cur.GetCurrentPropertyValue(AutomationElementIdentifiers.ControlTypeProperty) as ControlType; }
            catch { break; }

            if (ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.TreeItem)
            {
                string? name;
                try { name = cur.GetCurrentPropertyValue(AutomationElementIdentifiers.NameProperty) as string; }
                catch { name = null; }

                if (!string.IsNullOrEmpty(name))
                {
                    try
                    {
                        var rect = cur.GetCurrentPropertyValue(AutomationElementIdentifiers.BoundingRectangleProperty);
                        if (rect is System.Windows.Rect r && r.Width > 0 && r.Height > 0)
                            itemBounds = r;
                    }
                    catch { }
                    return SanitizeItemName(name);
                }
            }

            if (ct == ControlType.List || ct == ControlType.Group ||
                ct == ControlType.Pane || ct == ControlType.Window)
                break;

            try { cur = walker.GetParent(cur); }
            catch { break; }
        }
        return null;
    }

    private static string SanitizeItemName(string raw)
    {
        int nl = raw.IndexOfAny(['\r', '\n', '\t']);
        if (nl > 0) raw = raw[..nl];
        return raw.Trim();
    }

    // ── ユーティリティ ────────────────────────────────────────────────
    private static IntPtr FindCabinetAtPoint(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethodsExtra.WindowFromPoint(pt);
        var cur = hwnd;
        for (int i = 0; i < 12 && cur != IntPtr.Zero; i++)
        {
            string cls = GetClassNameStr(cur);
            if (cls == "CabinetWClass") return cur;
            // デスクトップアイコン（Progman / WorkerW）対応
            if (cls == "Progman" || cls == "WorkerW") return cur;
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return IntPtr.Zero;
    }

    private static string? GetFolderPathForCabinet(IntPtr cabinet)
    {
        // デスクトップアイコン（Progman / WorkerW）の場合はデスクトップパスを返す
        string cls = GetClassNameStr(cabinet);
        if (cls == "Progman" || cls == "WorkerW")
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

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

    // ── DPI変換ヘルパー ───────────────────────────────────────────────
    private static double GetDipScaleForCurrentMonitor(int dipX, int dipY)
    {
        try
        {
            var pt = new NativeMethods.POINT { X = dipX, Y = dipY };
            IntPtr hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return 1.0;
            const int MDT_EFFECTIVE_DPI = 0;
            int hr = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out uint _);
            if (hr == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch { }
        return 1.0;
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static System.Windows.Rect ScaleRect(System.Windows.Rect r, double scale) =>
        new(r.Left * scale, r.Top * scale, r.Width * scale, r.Height * scale);

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

    private static bool DetectDetailViewAt(NativeMethods.POINT pt)
    {
        try
        {
            IntPtr hwnd = NativeMethodsExtra.WindowFromPoint(pt);
            var cur = hwnd;
            for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
            {
                string cls = GetClassNameStr(cur);
                if (cls == "SysListView32")
                {
                    const int LVS_REPORT = 0x0001;
                    int style = GetWindowLong(cur, GWL_STYLE);
                    return (style & LVS_REPORT) != 0;
                }
                cur = NativeMethodsExtra.GetParent(cur);
            }

            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (el == null) return false;
            var walker = TreeWalker.ControlViewWalker;
            var target = el;
            for (int d = 0; d < 6 && target != null; d++)
            {
                var ct = target.GetCurrentPropertyValue(
                    AutomationElementIdentifiers.ControlTypeProperty) as ControlType;
                if (ct == ControlType.ListItem || ct == ControlType.DataItem)
                {
                    var rect = (System.Windows.Rect)target.GetCurrentPropertyValue(
                        AutomationElementIdentifiers.BoundingRectangleProperty);
                    return rect.Height > 0 && rect.Height < 40;
                }
                if (ct == ControlType.List || ct == ControlType.Pane ||
                    ct == ControlType.Window) break;
                try { target = walker.GetParent(target); } catch { break; }
            }
        }
        catch { }
        return false;
    }
}
