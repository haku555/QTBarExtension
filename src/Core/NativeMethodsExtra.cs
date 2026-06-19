using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QTBarExtension.Core;

/// <summary>
/// プレビュー機能・ホイールフック・ツールチップ用の追加 Win32 定義。
/// 既存の NativeMethods.cs を汚さないよう partial で分離。
/// </summary>
public static class NativeMethodsExtra
{
    // ── マウスフック (WH_MOUSE_LL) ─────────────────────────
    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEMOVE  = 0x0200;
    public const int WM_MOUSELEAVE = 0x02A3;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public NativeMethods.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // ── キーボードフック (WH_KEYBOARD_LL) ───────────────────
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN    = 0x0100;
    public const int WM_KEYUP      = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP   = 0x0105;
    public const int VK_LEFT  = 0x25;
    public const int VK_RIGHT = 0x27;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    // ── SysListView32 (詳細表示) 操作 ──────────────────────
    public const uint LVM_FIRST = 0x1000;
    public const uint LVM_GETITEMRECT = LVM_FIRST + 14;
    public const uint LVM_SUBITEMHITTEST = LVM_FIRST + 57;
    public const uint LVM_GETSUBITEMRECT = LVM_FIRST + 56;
    public const uint LVM_GETHEADER = LVM_FIRST + 31;
    public const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;
    public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    public const uint LVM_GETCOLUMNWIDTH = LVM_FIRST + 29;
    public const uint LVM_GETTOOLTIPS = LVM_FIRST + 78;

    public const int LVIR_LABEL = 2;
    public const int LVIR_BOUNDS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEMINDEX
    {
        public int iItem;
        public int iGroup;
    }

    /// <summary>
    /// LVITEMW (commctrl.h)。x64では mask/iItem/iSubItem/state/stateMask(4*5=20byte)の後、
    /// pszText(ポインタ)が8byte境界にアライメントされるため4byteパディングが入る。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    public const uint LVIF_TEXT = 0x0001;

    // ── プロセス間メモリ (LVM_GETITEMTEXT用) ───────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(NativeMethods.POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativeMethods.POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    // ── モニター情報 (プレビュー位置クランプ用) ─────────────
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ── DPI取得 ──────────────────────────────────────────────
    /// <summary>指定ウィンドウのDPIを取得 (Win10 1607+)</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>スクリーン座標からDIPへの変換係数 (96dpiが基準)</summary>
    public static double GetDipScale(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }
}
