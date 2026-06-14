using System;
using System.Collections.Generic;
using System.Text;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.Core;

public class ExplorerWindowInfo
{
    public IntPtr MainHwnd  { get; }
    public IntPtr ReBarHwnd { get; private set; }

    public ExplorerWindowInfo(IntPtr hwnd)
    {
        MainHwnd = hwnd;
        RefreshChildHandles();
    }

    public void RefreshChildHandles()
    {
        ReBarHwnd = IntPtr.Zero;
        EnumChildWindows(MainHwnd, (child, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(child, sb, 64);
            if (sb.ToString() == "ReBarWindow32") { ReBarHwnd = child; return false; }
            return true;
        }, IntPtr.Zero);
    }

    public bool TryGetBarInsertionPoint(out RECT mainRect, out int insertY)
    {
        mainRect = default; insertY = 0;
        if (ReBarHwnd == IntPtr.Zero) RefreshChildHandles();
        if (ReBarHwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(MainHwnd,  out mainRect))      return false;
        if (!GetWindowRect(ReBarHwnd, out var rebarRect)) return false;
        insertY = rebarRect.Bottom;
        return true;
    }

    public bool IsVisible => IsWindowVisible(MainHwnd) && !IsIconic(MainHwnd);

    /// <summary>
    /// 戦略: CabinetWClassの直接の子ウィンドウのうち
    /// ReBarWindow32以外のウィンドウ（コンテンツ領域）をすべて取得して下に押し込む。
    /// これによりWindowsバージョンや表示モードに関係なく確実に機能する。
    /// </summary>
    private List<IntPtr> GetContentWindows()
    {
        var result = new List<IntPtr>();
        EnumChildWindows(MainHwnd, (child, _) =>
        {
            // 直接の子のみ
            if (GetParent(child) != MainHwnd) return true;
            var sb = new StringBuilder(64);
            GetClassName(child, sb, 64);
            string cls = sb.ToString();
            // ReBarWindow32（アドレスバー）以外のウィンドウが対象
            if (cls != "ReBarWindow32" && IsWindowVisible(child))
                result.Add(child);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // 「バーが占有するピクセル数」を記録して正確に戻せるようにする
    private int _savedBarHeight;

    // 最後にPushした対象ウィンドウとバー高さを記録（ShellViewフック用）
    public int    LastBarHeight  { get; private set; }
    public int    LastInsertY    { get; private set; }
    public List<IntPtr> ContentWindowHandles { get; } = [];
    private bool _isPushing; // MoveWindow実行中フラグ（再入防止）

    public bool PushShellViewDown(int barInsertY, int barHeight)
    {
        // 再入防止: MoveWindow実行中にLOCATIONCHANGEが発火しても無視
        if (_isPushing) return false;
        _isPushing = true;
        try { return PushShellViewDownCore(barInsertY, barHeight); }
        finally { _isPushing = false; }
    }

    private bool PushShellViewDownCore(int barInsertY, int barHeight)
    {
        var targets = GetContentWindows();
        if (targets.Count == 0) return false;

        // barInsertY/barHeightはスクリーン座標
        int targetTopScreen = barInsertY + barHeight;
        bool pushed = false;

        foreach (var sv in targets)
        {
            if (!GetWindowRect(sv, out RECT svRect)) continue;

            // svRect.Top はスクリーン座標
            // ガード: 既にほぼ正しい位置（±3px）なら何もしない
            if (Math.Abs(svRect.Top - targetTopScreen) <= 3) continue;
            // 既にバーより下にある場合もスキップ
            if (svRect.Top >= targetTopScreen) continue;

            // スクリーン→クライアント座標変換
            var ptTopLeft = new POINT { X = svRect.Left, Y = targetTopScreen };
            ScreenToClient(MainHwnd, ref ptTopLeft);

            // 幅はクライアント座標でのウィンドウ幅を使う
            GetClientRect(MainHwnd, out RECT clientRect);
            int newW = clientRect.Width;  // 親のクライアント幅に合わせる
            int newH = svRect.Bottom - targetTopScreen;
            if (newH < 20) continue;

            MoveWindow(sv, ptTopLeft.X, ptTopLeft.Y, newW, newH, true);
            pushed = true;
        }
        if (pushed)
        {
            _savedBarHeight = barHeight;
            LastBarHeight   = barHeight;
            LastInsertY     = barInsertY;
            // 対象ウィンドウのHWNDを記録（フック監視用）
            ContentWindowHandles.Clear();
            ContentWindowHandles.AddRange(targets);
        }
        return pushed;
    }

    public void RestoreShellView(int barInsertY)
    {
        var targets = GetContentWindows();
        foreach (var sv in targets)
        {
            if (!GetWindowRect(sv, out RECT svRect)) continue;
            if (Math.Abs(svRect.Top - barInsertY) <= 3) continue;

            var pt = new POINT { X = svRect.Left, Y = barInsertY };
            ScreenToClient(MainHwnd, ref pt);

            GetClientRect(MainHwnd, out RECT clientRect);
            int newW = clientRect.Width;
            int newH = svRect.Bottom - barInsertY;
            if (newH < 20) continue;

            MoveWindow(sv, pt.X, pt.Y, newW, newH, true);
        }
        _savedBarHeight = 0;
    }

    public string GetCurrentPath()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return string.Empty;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            for (int i = 0; i < (int)windows.Count; i++)
            {
                try
                {
                    dynamic? win = windows.Item(i);
                    if (win == null) continue;
                    if ((IntPtr)(int)win.HWND == MainHwnd)
                        return (string?)win.LocationURL ?? string.Empty;
                }
                catch { }
            }
        }
        catch { }
        return string.Empty;
    }
}

public class ExplorerWatcher : IDisposable
{
    private readonly List<IntPtr> _hooks = [];
    private WinEventProc? _proc;
    private readonly Dictionary<IntPtr, ExplorerWindowInfo> _windows = [];
    private readonly object _lock = new();
    private bool _disposed;

    public event Action<ExplorerWindowInfo>? WindowAppeared;
    public event Action<ExplorerWindowInfo>? WindowMoved;
    public event Action<ExplorerWindowInfo>? WindowFocused;      // フォーカス取得時
    public event Action<IntPtr>?             WindowClosed;

    public IReadOnlyDictionary<IntPtr, ExplorerWindowInfo> Windows => _windows;

    private readonly Dictionary<IntPtr, uint> _lastLocationChange = [];
    private const uint ThrottleMs   = 80;   // Explorer本体の移動イベント用
    private const uint SvThrottleMs = 500;  // ShellView移動イベント用（リサイズ中の大量発火を抑制）
    private readonly Dictionary<IntPtr, uint> _lastSvChange = [];

    public ExplorerWatcher()
    {
        EnumWindows((hwnd, _) =>
        {
            if (IsCabinetWClass(hwnd) && IsWindowVisible(hwnd)) Register(hwnd);
            return true;
        }, IntPtr.Zero);

        _proc = OnWinEvent;

        void Hook(uint a, uint b) =>
            _hooks.Add(SetWinEventHook(a, b, IntPtr.Zero, _proc, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));

        Hook(EVENT_SYSTEM_FOREGROUND,     EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
        Hook(EVENT_OBJECT_SHOW,           EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_DESTROY,        EVENT_OBJECT_DESTROY);
        Hook(EVENT_SYSTEM_MINIMIZESTART,  EVENT_SYSTEM_MINIMIZEEND);
    }

    private ExplorerWindowInfo Register(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (!_windows.TryGetValue(hwnd, out var info))
            {
                info = new ExplorerWindowInfo(hwnd);
                _windows[hwnd] = info;
            }
            return info;
        }
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;

        if (ev == EVENT_OBJECT_LOCATIONCHANGE)
        {
            // ── CabinetWClass本体の移動/リサイズ ─────────────────
            if (idObject == 0 && IsCabinetWClass(hwnd))
            {
                lock (_lock)
                {
                    if (_lastLocationChange.TryGetValue(hwnd, out uint last) &&
                        time - last < ThrottleMs) return;
                    _lastLocationChange[hwnd] = time;
                }
                lock (_lock)
                {
                    if (_windows.TryGetValue(hwnd, out var info))
                        WindowMoved?.Invoke(info);
                    else if (IsWindowVisible(hwnd))
                        WindowAppeared?.Invoke(Register(hwnd));
                }
                return;
            }

            // ── ShellView等の子ウィンドウが動いた → 親Explorerに補正を通知 ──
            // _isPushingフラグで再入ループは防がれる
            if (idObject == 0)
            {
                lock (_lock)
                {
                    foreach (var kv in _windows)
                    {
                        if (!kv.Value.ContentWindowHandles.Contains(hwnd)) continue;
                        IntPtr owner = kv.Key;
                        // 専用スロットリング（300ms）
                        if (_lastSvChange.TryGetValue(owner, out uint last2) &&
                            time - last2 < 300) break;
                        _lastSvChange[owner] = time;
                        WindowMoved?.Invoke(kv.Value); // RepushShellViewをApp.csで呼ぶ
                        break;
                    }
                }
            }

            return;
        }

        if (ev == EVENT_OBJECT_SHOW || ev == EVENT_OBJECT_HIDE || ev == EVENT_OBJECT_DESTROY)
        {
            if (idObject != 0) return;
            if (!IsCabinetWClass(hwnd)) return;
            lock (_lock)
            {
                switch (ev)
                {
                    case EVENT_OBJECT_SHOW:
                        WindowAppeared?.Invoke(Register(hwnd));
                        break;
                    case EVENT_OBJECT_HIDE:
                    case EVENT_OBJECT_DESTROY:
                        if (_windows.ContainsKey(hwnd))
                        {
                            if (ev == EVENT_OBJECT_DESTROY) _windows.Remove(hwnd);
                            WindowClosed?.Invoke(hwnd);
                        }
                        break;
                }
            }
            return;
        }

        IntPtr explorerHwnd = IsCabinetWClass(hwnd) ? hwnd : FindExplorerAncestor(hwnd);
        if (explorerHwnd == IntPtr.Zero) return;

        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND:
                if (IsWindowVisible(explorerHwnd))
                {
                    var info = Register(explorerHwnd);
                    WindowFocused?.Invoke(info);   // フォーカス専用イベント
                    WindowAppeared?.Invoke(info);
                }
                break;
            case EVENT_SYSTEM_MINIMIZESTART:
                lock (_lock)
                {
                    if (_windows.ContainsKey(explorerHwnd))
                        WindowClosed?.Invoke(explorerHwnd);
                }
                break;
            case EVENT_SYSTEM_MINIMIZEEND:
                lock (_lock)
                {
                    if (_windows.TryGetValue(explorerHwnd, out var info))
                        WindowAppeared?.Invoke(info);
                }
                break;
        }
    }

    private static bool IsCabinetWClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, 64);
        return sb.ToString() == "CabinetWClass";
    }

    private static IntPtr FindExplorerAncestor(IntPtr hwnd)
    {
        var cur = GetParent(hwnd);
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            if (IsCabinetWClass(cur)) return cur;
            cur = GetParent(cur);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
