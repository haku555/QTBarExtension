using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using QTBarExtension.Core;
using static QTBarExtension.Core.NativeMethods;
using static QTBarExtension.Core.NativeMethodsExtra;

namespace QTBarExtension.Services;

/// <summary>
/// Explorerのフォルダビュー (SysListView32) を拡張するサービス。
///   - Shift+ホイールで横スクロール
///   - 詳細表示で省略されたファイル名をホバーで完全表示（独自ツールチップ）
///
/// 低レベルマウスフック (WH_MOUSE_LL) でホイール/移動イベントを監視し、
/// カーソル下の SysListView32 を WindowFromPoint で特定して
/// SendMessage / ReadProcessMemory 経由で操作する。
/// </summary>
public class ExplorerViewEnhancer : IDisposable
{
    private readonly Models.AppSettings _settings;
    private LowLevelMouseProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;

    // フルネームツールチップ用
    private readonly DispatcherTimer _hoverTimer;
    private NativeMethods.POINT _lastMovePt;
    private IntPtr _lastHoverListView = IntPtr.Zero;
    private int _lastHoverItem = -1;
    private FullNameTooltip? _tooltip;

    public ExplorerViewEnhancer(Models.AppSettings settings)
    {
        _settings = settings;

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _hoverTimer.Tick += OnHoverTimerTick;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(curModule.ModuleName!), 0);
        _hoverTimer.Start();
    }

    public void Stop()
    {
        _hoverTimer.Stop();
        HideTooltip();
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // ── マウスフックコールバック ───────────────────────────
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            if (msg == WM_MOUSEWHEEL && _settings.EnableShiftWheelHorizontalScroll)
            {
                if ((Keyboard.IsShiftPressed()))
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (TryHandleShiftWheel(data))
                        return new IntPtr(1); // イベントを消費（縦スクロールを抑制）
                }
            }
            else if (msg == WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _lastMovePt = data.pt;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ── Shift+ホイール横スクロール ─────────────────────────
    private bool TryHandleShiftWheel(MSLLHOOKSTRUCT data)
    {
        IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(data.pt);
        IntPtr listView = FindListViewAncestor(hwndUnder);
        if (listView == IntPtr.Zero) return false;

        // ホイール方向取得 (mouseData の上位16bit, 120単位)
        int delta = (short)((data.mouseData >> 16) & 0xFFFF);

        // SysListView32 は WM_HSCROLL に対応していないため、
        // 水平スクロールバーへ SB_LINELEFT / SB_LINERIGHT メッセージを送る。
        // より確実な方法として WM_MOUSEHWHEEL を直接転送する。
        const uint WM_MOUSEHWHEEL = 0x020E;

        // 横ホイール量はシステム設定の3倍程度に増幅して快適なスクロール量にする
        int amplified = delta * 1; // delta(120単位)をそのまま渡す → Explorer側で1クリック分処理
        IntPtr wParamOut = new IntPtr((amplified << 16));
        IntPtr lParamOut = MakeLParam(data.pt.X, data.pt.Y);

        try
        {
            // 子ウィンドウ自体に直接送る（スクリーン座標→クライアント座標変換が必要なものもあるが
            // WM_MOUSEHWHEEL はスクリーン座標で良い）
            PostMessage(listView, WM_MOUSEHWHEEL, wParamOut, lParamOut);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr MakeLParam(int x, int y) => new IntPtr((y << 16) | (x & 0xFFFF));

    // ── フルネームツールチップ ──────────────────────────────
    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        if (!_settings.EnableFullNameTooltip)
        {
            HideTooltip();
            return;
        }

        IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(_lastMovePt);
        IntPtr listView = FindListViewAncestor(hwndUnder);
        if (listView == IntPtr.Zero)
        {
            HideTooltip();
            _lastHoverListView = IntPtr.Zero;
            _lastHoverItem = -1;
            return;
        }

        // 詳細表示でなければ何もしない（拡張子付きファイル名カラムのみ対象）
        var pt = _lastMovePt;
        if (!ScreenToClient(listView, ref pt)) return;

        // SubItemHitTest で行/列を特定
        var hti = new LVHITTESTINFO { pt = pt };
        int hitResult = SendLvHitTest(listView, ref hti);
        if (hitResult < 0)
        {
            HideTooltip();
            _lastHoverItem = -1;
            return;
        }

        if (listView == _lastHoverListView && hti.iItem == _lastHoverItem && _tooltip != null)
            return; // 同じ項目 → 何もしない

        _lastHoverListView = listView;
        _lastHoverItem = hti.iItem;

        // アイテムのラベル矩形と表示テキストを取得
        if (!TryGetItemLabelRect(listView, hti.iItem, out RECT labelRect))
        {
            HideTooltip();
            return;
        }

        string fullText = GetItemText(listView, hti.iItem, hti.iSubItem);
        if (string.IsNullOrEmpty(fullText))
        {
            HideTooltip();
            return;
        }

        // テキストが省略されているかどうかの簡易判定:
        // ラベル矩形の幅に対し、文字数 * 推定文字幅 が大きければ省略中とみなす。
        // 正確な判定はGDI計測が必要だが、簡易的にラベル幅で代用し、
        // 常に表示してもユーザー体験上は問題ない（短い名前は枠内に収まりツールチップも短く出るのみ）。
        double estCharWidth = 7.0; // 既定UIフォントの平均文字幅(px)概算
        double estTextWidth = fullText.Length * estCharWidth;
        if (estTextWidth <= labelRect.Width + 4)
        {
            HideTooltip(); // 省略されていない
            return;
        }

        // 表示位置: ラベル矩形の左上をスクリーン座標に変換
        var screenPt = new NativeMethods.POINT { X = labelRect.Left, Y = labelRect.Top };
        ClientToScreen(listView, ref screenPt);

        ShowTooltip(fullText, screenPt.X, screenPt.Y, labelRect.Height);
    }

    private void ShowTooltip(string text, int screenX, int screenY, int rowHeight)
    {
        if (_tooltip == null)
        {
            _tooltip = new FullNameTooltip();
        }
        _tooltip.SetText(text);
        _tooltip.MoveTo(screenX, screenY - 2, rowHeight);
        if (!_tooltip.IsVisible) _tooltip.Show();
    }

    private void HideTooltip()
    {
        if (_tooltip != null && _tooltip.IsVisible)
        {
            try { _tooltip.Hide(); } catch { }
        }
    }

    // ── SysListView32 探索 ──────────────────────────────────
    private static IntPtr FindListViewAncestor(IntPtr hwnd)
    {
        var cur = hwnd;
        for (int i = 0; i < 6 && cur != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(64);
            NativeMethodsExtra.GetClassName(cur, sb, 64);
            if (sb.ToString() == "SysListView32" || sb.ToString() == "DirectUIHWND")
            {
                if (sb.ToString() == "SysListView32") return cur;
                // DirectUIHWND の子に SysListView32 がある場合 (Win11標準)
                var child = FindChildListView(cur);
                if (child != IntPtr.Zero) return child;
            }
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindChildListView(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            var sb = new StringBuilder(64);
            NativeMethodsExtra.GetClassName(h, sb, 64);
            if (sb.ToString() == "SysListView32") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ── LVM メッセージ (クロスプロセス) ─────────────────────
    private static int SendLvHitTest(IntPtr listView, ref LVHITTESTINFO hti)
    {
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return -1;
        try
        {
            int size = Marshal.SizeOf<LVHITTESTINFO>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)size, MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return -1;
            try
            {
                byte[] buf = StructToBytes(hti);
                if (!WriteProcessMemory(hProc, remote, buf, buf.Length, out _)) return -1;

                IntPtr result = SendMessage(listView, LVM_SUBITEMHITTEST, IntPtr.Zero, remote);

                byte[] outBuf = new byte[size];
                if (!ReadProcessMemory(hProc, remote, outBuf, size, out _)) return -1;
                hti = BytesToStruct<LVHITTESTINFO>(outBuf);

                return result.ToInt32();
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    private static bool TryGetItemLabelRect(IntPtr listView, int item, out RECT rect)
    {
        rect = default;
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return false;
        try
        {
            int size = Marshal.SizeOf<RECT>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)size, MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;
            try
            {
                var inRect = new RECT { Left = LVIR_LABEL };
                byte[] buf = StructToBytes(inRect);
                if (!WriteProcessMemory(hProc, remote, buf, buf.Length, out _)) return false;

                IntPtr result = SendMessage(listView, LVM_GETITEMRECT, new IntPtr(item), remote);
                if (result == IntPtr.Zero) return false;

                byte[] outBuf = new byte[size];
                if (!ReadProcessMemory(hProc, remote, outBuf, size, out _)) return false;
                rect = BytesToStruct<RECT>(outBuf);
                return true;
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    private static string GetItemText(IntPtr listView, int item, int subItem)
    {
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return "";
        try
        {
            const int bufChars = 512;
            int bufBytes = bufChars * 2; // wchar
            int itemStructSize = Marshal.SizeOf<LVITEMW>();
            int total = itemStructSize + bufBytes;

            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)total, MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return "";
            try
            {
                IntPtr textPtr = remote + itemStructSize;

                var lvItem = new LVITEMW
                {
                    mask = LVIF_TEXT,
                    iItem = item,
                    iSubItem = subItem,
                    pszText = textPtr,
                    cchTextMax = bufChars,
                };

                byte[] itemBuf = StructToBytes(lvItem);
                if (!WriteProcessMemory(hProc, remote, itemBuf, itemBuf.Length, out _)) return "";

                SendMessage(listView, LVM_GETITEMTEXTW, new IntPtr(item), remote);

                byte[] textBuf = new byte[bufBytes];
                if (!ReadProcessMemory(hProc, textPtr, textBuf, bufBytes, out _)) return "";

                string s = Encoding.Unicode.GetString(textBuf);
                int nul = s.IndexOf('\0');
                if (nul >= 0) s = s[..nul];
                return s;
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] arr = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(s, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return arr;
    }

    private static T BytesToStruct<T>(byte[] arr) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(arr, 0, ptr, size);
            return Marshal.PtrToStructure<T>(ptr)!;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
}

/// <summary>Shift キー押下状態の取得</summary>
internal static class Keyboard
{
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
    private const int VK_SHIFT = 0x10;
    public static bool IsShiftPressed() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;
}
