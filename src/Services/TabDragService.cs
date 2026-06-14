using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QTBarExtension.UI;

namespace QTBarExtension.Services;

/// <summary>
/// Explorer間のタブドラッグを仲介するシングルトンサービス。
/// ドラッグ開始側がStartDragを呼び、マウス追跡タイマーでゴーストを動かす。
/// ドロップ先のOverlayBarがAcceptDropを呼んでタブを受け取る。
/// </summary>
public class TabDragService : IDisposable
{
    public static readonly TabDragService Instance = new();

    // ドラッグ中のタブ情報
    public bool      IsDragging    { get; private set; }
    private double _offsetX, _offsetY; // カーソルからゴースト左上へのオフセット
    public string    DragTabUrl    { get; private set; } = "";
    public string    DragTabLabel  { get; private set; } = "";
    public OverlayBar? DragSource  { get; private set; }

    // ゴーストウィンドウ
    private DragGhostWindow? _ghost;
    private readonly DispatcherTimer _tracker;

    // ドロップ受け入れコールバック
    public event Action<OverlayBar, string>? TabDropped; // (dropper, url)
    public event Action<OverlayBar>?         DragCancelled;

    private TabDragService()
    {
        _tracker = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
        };
        _tracker.Tick += OnTrackerTick;
    }

    public void StartDrag(OverlayBar source, string url, string label, Color accentColor, double initScreenX = 0, double initScreenY = 0, double tabWidth = 140, double tabHeight = 26, bool isDark = false)
    {
        if (IsDragging) CancelDrag();

        DragSource   = source;
        DragTabUrl   = url;
        DragTabLabel = label;
        IsDragging   = true;

        _ghost = new DragGhostWindow(label, accentColor, tabWidth, tabHeight, isDark);
        _ghost.Show();
        // 初期位置: タブの左上から生成
        // カーソル位置とタブ左上の差をオフセットとして保持
        Core.NativeMethods.GetCursorPos(out var curPt);
        _offsetX = initScreenX - curPt.X; // 通常は負値（カーソルはタブ内にある）
        _offsetY = initScreenY - curPt.Y;
        _ghost.MoveTo(initScreenX, initScreenY);
        _tracker.Start();
    }

    private void OnTrackerTick(object? sender, EventArgs e)
    {
        if (!IsDragging || _ghost == null) return;

        // スクリーン座標でカーソル位置を取得
        Core.NativeMethods.GetCursorPos(out var pt);
        // オフセット込みでタブ生成位置からの相対追従
        _ghost.MoveTo(pt.X + _offsetX, pt.Y + _offsetY);

        // ドラッグ元BarのインジケーターをUIスレッドで更新
        DragSource?.UpdateDragIndicator();

        // マウスボタンが離されたらドロップ処理
        if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            HandleDrop(pt.X, pt.Y);
        }
    }

    private void HandleDrop(int screenX, int screenY)
    {
        _tracker.Stop();
        CloseGhost();

        if (!IsDragging) return;
        IsDragging = false;

        // カーソル下のウィンドウを取得
        var pt = new Core.NativeMethods.POINT { X = screenX, Y = screenY };
        IntPtr hwndUnder = Core.NativeMethods.WindowFromPoint(pt);

        // ドロップ先のOverlayBarを探す
        OverlayBar? target = FindBarAt(hwndUnder);
        var source = DragSource;
        DragSource = null;

        if (source == null) return;

        if (target == source)
        {
            // 同じBar内ドロップ → タブ並べ替え確定
            // ただしタブチップ以外（空白部分）へのドロップは複製
            // タブチップへのドロップは並べ替えリアルタイム済み
            // → ドロップ位置がタブチップ外かどうかはBarに判断させる
            TabDropped?.Invoke(source, "__SAME__:" + DragTabUrl);
        }
        else if (target != null)
        {
            // 別のBarにドロップ → タブ移動
            TabDropped?.Invoke(target, DragTabUrl);
        }
        else
        {
            // Bar外ドロップ → 新ウィンドウとして分離
            TabDropped?.Invoke(source, "__DETACH__:" + DragTabUrl);
        }
    }

    public void CancelDrag()
    {
        _tracker.Stop();
        CloseGhost();
        if (IsDragging)
        {
            var src = DragSource;
            IsDragging = false;
            DragSource = null;
            if (src != null) DragCancelled?.Invoke(src);
        }
    }

    private void CloseGhost()
    {
        if (_ghost != null)
        {
            try { _ghost.Close(); } catch { }
            _ghost = null;
        }
    }

    private static OverlayBar? FindBarAt(IntPtr hwnd)
    {
        // HWNDからOverlayBarを探す（App.csが登録したリストを参照）
        return BarRegistry.FindByHwndOrAncestor(hwnd);
    }

    public void Dispose()
    {
        _tracker.Stop();
        CloseGhost();
    }
}

/// <summary>全OverlayBarのHWNDを登録するレジストリ</summary>
public static class BarRegistry
{
    private static readonly System.Collections.Generic.Dictionary<IntPtr, OverlayBar> _map = new();

    public static void Register(IntPtr hwnd, OverlayBar bar) => _map[hwnd] = bar;
    public static void Unregister(IntPtr hwnd) => _map.Remove(hwnd);

    public static OverlayBar? FindByHwndOrAncestor(IntPtr hwnd)
    {
        var cur = hwnd;
        for (int i = 0; i < 12 && cur != IntPtr.Zero; i++)
        {
            if (_map.TryGetValue(cur, out var bar)) return bar;
            cur = Core.NativeMethods.GetParent(cur);
        }
        return null;
    }
}
