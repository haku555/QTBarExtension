using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using QTBarExtension.Core;
using QTBarExtension.Models;
using QTBarExtension.UI;
using static QTBarExtension.Core.NativeMethods;
using static QTBarExtension.Core.NativeMethodsExtra;

namespace QTBarExtension.Services;

/// <summary>
/// サブフォルダメニュー機能を提供するサービス。
/// 
/// ① フォルダビュー上チップ表示:
///    マウスフックでExplorer内フォルダアイコンのホバーを検知し、
///    アイコンの右下（縮小表示）または右隣（詳細表示）に小さな▶チップを表示。
///    チップをクリックするとサブフォルダメニューが開く。
///
/// ② タブアイコン クリックでサブフォルダメニュー:
///    EnableTabIconSubFolder=true のとき、OverlayBar がタブのフォルダアイコン
///    クリックを受けて ShowForTab() を呼ぶ。
///    右クリックで上位階層フォルダのメニューを表示する。
///
/// ③ D&amp;D 遅延展開:
///    ファイルドラッグ中にフォルダ上で DragOpenDelayMs 待機後にメニュー表示。
/// </summary>
public class SubFolderMenuService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly PreviewContentProvider _previewProvider;

    // ── 現在開いているメニュー ─────────────────────────────────
    private SubFolderMenuWindow? _currentMenu;
    private string? _currentMenuPath;

    // ── フォルダビュー上チップ ────────────────────────────────
    private SubFolderChipWindow? _chip;          // チップウィンドウ（常駐）
    private string?  _chipFolderPath;            // 現在チップが指しているフォルダ
    private IntPtr   _chipListView = IntPtr.Zero;
    private int      _chipItemIndex = -1;
    private readonly DispatcherTimer _chipHoverTimer;
    private NativeMethods.POINT _lastMousePt;

    // ── D&D 遅延タイマー ──────────────────────────────────────
    private readonly DispatcherTimer _dragTimer;
    private string? _pendingDragPath;
    private NativeMethods.POINT _lastDragPt;

    // ── マウスフック ──────────────────────────────────────────
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    // ── 公開イベント（OverlayBarへの通知用）──────────────────
    /// <summary>フォルダビュー上のメニューが開いた／閉じた時に通知</summary>
    public event Action<string?>? MenuStateChanged;

    public SubFolderMenuService(AppSettings settings, PreviewContentProvider previewProvider)
    {
        _settings        = settings;
        _previewProvider = previewProvider;
        _dispatcher      = Dispatcher.CurrentDispatcher;

        _dragTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(100, settings.SubFolderMenu.DragOpenDelayMs))
        };
        _dragTimer.Tick += OnDragTimerTick;

        // チップ表示タイマー（150ms 後に現在ホバー中アイテムのチップを更新）
        _chipHoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _chipHoverTimer.Tick += OnChipHoverTimerTick;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        _hookProc = HookCallback;
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        using var mod  = proc.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc,
            GetModuleHandle(mod.ModuleName!), 0);

        _chipHoverTimer.Start();
    }

    public void Stop()
    {
        _chipHoverTimer.Stop();
        _dragTimer.Stop();
        HideChip();
        CloseCurrentMenu();
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    // ── マウスフックコールバック ──────────────────────────────
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _settings.SubFolderMenu.Enabled)
        {
            int msg = wParam.ToInt32();

            if (msg == WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _lastMousePt = data.pt;
                // チップ更新はタイマーが担当（150ms デバウンス）
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ── チップホバータイマー ─────────────────────────────────
    private void OnChipHoverTimerTick(object? sender, EventArgs e)
    {
        if (!_settings.SubFolderMenu.Enabled) { HideChip(); return; }
        if (!_settings.SubFolderMenu.ShowFolderViewChip) { HideChip(); return; }

        // チップ自体にカーソルがある場合は消さない
        if (_chip != null && _chip.IsMouseOver) return;
        // 現在開いているメニューの上にある場合も消さない
        if (_currentMenu != null && _currentMenu.IsVisible) return;

        var pt = _lastMousePt;

        // カーソル下の ListView を探す
        IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(pt);
        IntPtr listView  = FindListViewAncestor(hwndUnder);

        if (listView == IntPtr.Zero)
        {
            // ListView 外 → チップを消す
            if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver)
                HideChip();
            return;
        }

        // フォーカス不在チェック
        if (!_settings.SubFolderMenu.ShowWhenInactive)
        {
            IntPtr fg = GetForegroundWindow();
            if (!IsSameTopLevel(hwndUnder, fg)) { HideChip(); return; }
        }

        // ListViewのクライアント座標に変換
        var cpt = new NativeMethods.POINT { X = pt.X, Y = pt.Y };
        ScreenToClient(listView, ref cpt);

        // LVM_SUBITEMHITTEST でアイテム/サブアイテムを取得
        int itemIdx = SendLvHitTest(listView, cpt, out int subItem);
        if (itemIdx < 0)
        {
            if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver)
                HideChip();
            return;
        }

        // 同じアイテムなら更新不要
        if (listView == _chipListView && itemIdx == _chipItemIndex) return;

        // アイテムのテキスト取得（ファイル名）
        string itemName = GetItemText(listView, itemIdx, 0);
        if (string.IsNullOrEmpty(itemName)) { HideChip(); return; }

        // アイテムがフォルダかどうか判定（Shell.Applicationは重いので属性で判定）
        // フォルダ一覧上で表示パスを取得してフルパスを構成
        string? folderPath = GetFolderPathFromListView(listView);
        if (folderPath == null) { HideChip(); return; }

        string fullPath = Path.Combine(folderPath, itemName);

        // フォルダでなければ（EnableForZip=true ならzipも対象）
        bool isFolder = Directory.Exists(fullPath);
        bool isZip    = !isFolder && fullPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && _settings.SubFolderMenu.EnableForZip;
        if (!isFolder && !isZip) { HideChip(); return; }

        // アイテムのアイコン矩形を取得してチップ位置を決める
        if (!TryGetItemIconRect(listView, itemIdx, out var iconRect)) { HideChip(); return; }

        // スクリーン座標に変換
        var iconTopLeft = new NativeMethods.POINT { X = iconRect.Left, Y = iconRect.Top };
        ClientToScreen(listView, ref iconTopLeft);

        // ListView のスタイルからビューモードを判定
        // LVS_REPORT(0x0001)=詳細表示, LVS_ICON(0x0000)=縮小表示
        bool isDetailView = IsDetailView(listView);

        double chipX, chipY;
        if (isDetailView)
        {
            // 詳細表示: アイコンの右隣
            chipX = iconTopLeft.X + iconRect.Width + 1;
            chipY = iconTopLeft.Y + (iconRect.Height - 14) / 2.0;
        }
        else
        {
            // 縮小表示（アイコン表示）: アイコンの右下
            chipX = iconTopLeft.X + iconRect.Width - 10;
            chipY = iconTopLeft.Y + iconRect.Height - 10;
        }

        _chipListView  = listView;
        _chipItemIndex = itemIdx;
        _chipFolderPath = fullPath;

        ShowChip(chipX, chipY, fullPath);
    }

    private void ShowChip(double screenX, double screenY, string folderPath)
    {
        if (_chip == null)
        {
            _chip = new SubFolderChipWindow();
            _chip.ChipClicked += (path, x, y) =>
            {
                // チップクリック → サブフォルダメニューを開く
                OpenMenuAt(path, x, y, IntPtr.Zero);
            };
        }
        _chip.ShowAt(screenX, screenY, folderPath);
    }

    private void HideChip()
    {
        _chipListView  = IntPtr.Zero;
        _chipItemIndex = -1;
        _chipFolderPath = null;
        _chip?.HideChip();
    }

    // ── タブアイコン クリックからの呼び出し ───────────────────
    /// <summary>
    /// タブのフォルダアイコンをクリックしたときにサブフォルダメニューを表示する。
    /// OverlayBar のアイコン MouseLeftButtonDown から呼ぶ。
    /// </summary>
    public void ShowForTabIcon(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (!_settings.SubFolderMenu.EnableTabIconSubFolder) return;
        if (string.IsNullOrEmpty(folderPath)) return;
        OpenMenuAt(folderPath, screenX, screenY, explorerHwnd);
    }

    /// <summary>
    /// タブのフォルダアイコンを右クリックしたときに上位階層のメニューを表示する。
    /// </summary>
    public void ShowParentForTabIcon(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (!_settings.SubFolderMenu.EnableTabIconSubFolder) return;
        string? parent = Path.GetDirectoryName(folderPath.TrimEnd('\\', '/'));
        if (parent == null || parent == folderPath) return;
        OpenMenuAt(parent, screenX, screenY, explorerHwnd);
    }

    // ── タブホバー（旧 ShowForTab）─────────────────────────────
    /// <summary>
    /// タブをホバーしたときにサブフォルダメニューを表示する（将来拡張用に残す）。
    /// 現在の設計ではタブアイコンクリックを推奨するため、このメソッドは未使用。
    /// </summary>
    public void ShowForTab(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (string.IsNullOrEmpty(folderPath)) return;
        if (folderPath == _currentMenuPath && _currentMenu?.IsVisible == true) return;
        OpenMenuAt(folderPath, screenX, screenY, explorerHwnd);
    }

    /// <summary>メニューを閉じる</summary>
    public void CloseMenu() => CloseCurrentMenu();

    // ── メニュー生成 ─────────────────────────────────────────
    private void OpenMenuAt(string rootPath, double screenX, double screenY, IntPtr explorerHwnd)
    {
        CloseCurrentMenu();
        _currentMenuPath = rootPath;

        Task.Run(() =>
        {
            var items = LoadItems(rootPath);
            _dispatcher.InvokeAsync(() =>
            {
                if (_currentMenuPath != rootPath) return; // 割り込みキャンセル
                _currentMenu = new SubFolderMenuWindow(
                    items, rootPath, _settings.SubFolderMenu, _previewProvider,
                    onNavigate: path =>
                    {
                        CloseCurrentMenu();
                        if (explorerHwnd != IntPtr.Zero)
                            ShellHelper.NavigateTo(explorerHwnd, path);
                        else
                            try { System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo(path)
                                { UseShellExecute = true }); }
                            catch { }
                        MenuStateChanged?.Invoke(null);
                    },
                    onOpenSubmenu: (path, x, y) => OpenMenuAt(path, x, y, explorerHwnd)
                );
                _currentMenu.Closed += (_, _) =>
                {
                    _currentMenu = null;
                    _currentMenuPath = null;
                    MenuStateChanged?.Invoke(null);
                };
                _currentMenu.ShowAt(screenX, screenY);
                MenuStateChanged?.Invoke(rootPath);
            });
        });
    }

    // ── D&D 遅延タイマー ─────────────────────────────────────
    private void OnDragTimerTick(object? sender, EventArgs e)
    {
        _dragTimer.Stop();
        if (_pendingDragPath == null) return;
        OpenMenuAt(_pendingDragPath, _lastDragPt.X + 20, _lastDragPt.Y, IntPtr.Zero);
    }

    // ── アイテム読み込み ─────────────────────────────────────
    public List<SubFolderItem> LoadItems(string path)
    {
        var cfg   = _settings.SubFolderMenu;
        var items = new List<SubFolderItem>();

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && cfg.EnableForZip)
            return LoadZipItems(path);

        if (!Directory.Exists(path)) return items;

        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible    = true,
                RecurseSubdirectories = false,
                AttributesToSkip      = FileAttributes.None,
            };

            foreach (var dir in Directory.EnumerateDirectories(path, "*", opts))
            {
                try
                {
                    var attr = File.GetAttributes(dir);
                    if (!cfg.ShowHidden && attr.HasFlag(FileAttributes.Hidden)) continue;
                    if (!cfg.ShowSystem && attr.HasFlag(FileAttributes.System)) continue;
                    items.Add(new SubFolderItem
                    {
                        Name        = Path.GetFileName(dir),
                        FullPath    = dir,
                        IsDirectory = true,
                        HasChildren = HasAnyChild(dir, cfg),
                        Modified    = Directory.GetLastWriteTime(dir),
                    });
                }
                catch { }
            }

            if (cfg.ShowFiles)
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", opts))
                {
                    try
                    {
                        var attr = File.GetAttributes(file);
                        if (!cfg.ShowHidden && attr.HasFlag(FileAttributes.Hidden)) continue;
                        if (!cfg.ShowSystem && attr.HasFlag(FileAttributes.System)) continue;
                        var fi = new FileInfo(file);
                        items.Add(new SubFolderItem
                        {
                            Name        = fi.Name,
                            FullPath    = file,
                            IsDirectory = false,
                            Extension   = fi.Extension.ToLowerInvariant(),
                            SizeBytes   = fi.Length,
                            Modified    = fi.LastWriteTime,
                            IsZip       = fi.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                    catch { }
                }
            }
        }
        catch { }

        return SortItems(items, cfg.SortMode);
    }

    private List<SubFolderItem> LoadZipItems(string zipPath)
    {
        var items = new List<SubFolderItem>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/').TrimEnd('/');
                int slash = name.IndexOf('/');
                string top = slash < 0 ? name : name[..slash];
                if (!string.IsNullOrEmpty(top)) topLevel.Add(top);
            }
            foreach (var name in topLevel.OrderBy(n => n))
            {
                bool isDir = zip.Entries.Any(e =>
                    e.FullName.Replace('\\', '/').StartsWith(
                        name + "/", StringComparison.OrdinalIgnoreCase));
                items.Add(new SubFolderItem
                {
                    Name           = name,
                    FullPath       = zipPath + "\\" + name,
                    IsDirectory    = isDir,
                    HasChildren    = isDir,
                    Extension      = isDir ? "" : Path.GetExtension(name).ToLowerInvariant(),
                    IsArchiveEntry = true,
                    ArchivePath    = zipPath,
                });
            }
        }
        catch { }
        return items;
    }

    private static bool HasAnyChild(string path, SubFolderMenuSettings cfg)
    {
        try
        {
            var opts = new EnumerationOptions
                { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None };
            if (Directory.EnumerateDirectories(path, "*", opts).Any()) return true;
            if (cfg.ShowFiles && Directory.EnumerateFiles(path, "*", opts).Any()) return true;
        }
        catch { }
        return false;
    }

    private static List<SubFolderItem> SortItems(
        List<SubFolderItem> items, SubFolderSortMode mode)
    {
        var dirs  = items.Where(i =>  i.IsDirectory).ToList();
        var files = items.Where(i => !i.IsDirectory).ToList();

        IEnumerable<SubFolderItem> Sort(List<SubFolderItem> g) => mode switch
        {
            SubFolderSortMode.Name         => g.OrderBy(i => i.Name,
                                                StringComparer.CurrentCultureIgnoreCase),
            SubFolderSortMode.ModifiedDesc => g.OrderByDescending(i => i.Modified),
            SubFolderSortMode.ModifiedAsc  => g.OrderBy(i => i.Modified),
            SubFolderSortMode.Extension    => g.OrderBy(i => i.Extension)
                                               .ThenBy(i => i.Name,
                                                    StringComparer.CurrentCultureIgnoreCase),
            _                              => g.AsEnumerable(),
        };

        return Sort(dirs).Concat(Sort(files)).ToList();
    }

    // ── Win32 ヘルパー ───────────────────────────────────────
    private static IntPtr FindListViewAncestor(IntPtr hwnd)
    {
        var cur = hwnd;
        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(64);
            GetClassName(cur, sb, 64);
            string cls = sb.ToString();
            if (cls == "SysListView32") return cur;
            if (cls == "DirectUIHWND")
            {
                var child = FindChildListView(cur);
                if (child != IntPtr.Zero) return child;
            }
            cur = GetParent(cur);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindChildListView(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            if (sb.ToString() == "SysListView32") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// ListView のウィンドウスタイルからビューモードを判定する。
    /// LVS_REPORT (0x0001) が立っていれば詳細表示。
    /// </summary>
    private static bool IsDetailView(IntPtr listView)
    {
        const int GWL_STYLE = -16;
        const int LVS_REPORT = 0x0001;
        int style = GetWindowLong(listView, GWL_STYLE);
        return (style & LVS_REPORT) != 0;
    }

    /// <summary>アイテムのアイコン矩形（クライアント座標）を取得</summary>
    private static bool TryGetItemIconRect(IntPtr listView, int item, out RECT rect)
    {
        rect = default;
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return false;
        try
        {
            int size = Marshal.SizeOf<RECT>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)size,
                MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;
            try
            {
                // LVIR_ICON = 1
                var inRect = new RECT { Left = 1 };
                byte[] buf = StructToBytes(inRect);
                if (!WriteProcessMemory(hProc, remote, buf, buf.Length, out _)) return false;

                IntPtr result = SendMessage(listView, LVM_GETITEMRECT,
                    new IntPtr(item), remote);
                if (result == IntPtr.Zero) return false;

                byte[] outBuf = new byte[size];
                if (!ReadProcessMemory(hProc, remote, outBuf, size, out _)) return false;
                rect = BytesToStruct<RECT>(outBuf);
                return rect.Width > 0;
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    private static int SendLvHitTest(IntPtr listView, NativeMethods.POINT clientPt,
        out int subItem)
    {
        subItem = 0;
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return -1;
        try
        {
            int size = Marshal.SizeOf<LVHITTESTINFO>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)size,
                MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return -1;
            try
            {
                var hti = new LVHITTESTINFO { pt = clientPt };
                byte[] buf = StructToBytes(hti);
                if (!WriteProcessMemory(hProc, remote, buf, buf.Length, out _)) return -1;

                IntPtr result = SendMessage(listView, LVM_SUBITEMHITTEST,
                    IntPtr.Zero, remote);

                byte[] outBuf = new byte[size];
                if (!ReadProcessMemory(hProc, remote, outBuf, size, out _)) return -1;
                var outHti = BytesToStruct<LVHITTESTINFO>(outBuf);
                subItem = outHti.iSubItem;
                return result.ToInt32();
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    private static string GetItemText(IntPtr listView, int item, int sub)
    {
        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return "";
        try
        {
            const int chars = 512;
            int bufBytes = chars * 2;
            int itemSize = Marshal.SizeOf<LVITEMW>();
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero,
                (uint)(itemSize + bufBytes), MEM_COMMIT, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return "";
            try
            {
                IntPtr textPtr = remote + itemSize;
                var lvi = new LVITEMW
                {
                    mask = LVIF_TEXT, iItem = item, iSubItem = sub,
                    pszText = textPtr, cchTextMax = chars,
                };
                byte[] lviBytes = StructToBytes(lvi);
                if (!WriteProcessMemory(hProc, remote, lviBytes, lviBytes.Length, out _))
                    return "";
                SendMessage(listView, LVM_GETITEMTEXTW, new IntPtr(item), remote);

                byte[] textBuf = new byte[bufBytes];
                if (!ReadProcessMemory(hProc, textPtr, textBuf, bufBytes, out _)) return "";

                string s = System.Text.Encoding.Unicode.GetString(textBuf);
                int nul = s.IndexOf('\0');
                return nul >= 0 ? s[..nul] : s;
            }
            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>ListView が属する Explorer の現在のフォルダパスを取得</summary>
    private static string? GetFolderPathFromListView(IntPtr listView)
    {
        // CabinetWClass まで遡る
        IntPtr cabinet = listView;
        for (int i = 0; i < 12 && cabinet != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(64);
            GetClassName(cabinet, sb, 64);
            if (sb.ToString() == "CabinetWClass") break;
            cabinet = GetParent(cabinet);
        }
        if (cabinet == IntPtr.Zero) return null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell   = Activator.CreateInstance(shellType)!;
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
            cur = GetParent(cur);
        }
        return false;
    }

    private static byte[] StructToBytes<T>(T s) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] arr = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(s, ptr, false); Marshal.Copy(ptr, arr, 0, size); }
        finally { Marshal.FreeHGlobal(ptr); }
        return arr;
    }

    private static T BytesToStruct<T>(byte[] arr) where T : struct
    {
        IntPtr ptr = Marshal.AllocHGlobal(arr.Length);
        try { Marshal.Copy(arr, 0, ptr, arr.Length); return Marshal.PtrToStructure<T>(ptr)!; }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private void CloseCurrentMenu()
    {
        try { _currentMenu?.Close(); } catch { }
        _currentMenu    = null;
        _currentMenuPath = null;
    }

    // NativeMethods aliases（NativeMethodsExtra にも同名があるため明示）
    private static IntPtr GetForegroundWindow() => NativeMethodsExtra.GetForegroundWindow();
    private static void GetClassName(IntPtr h, StringBuilder sb, int n)
        => NativeMethodsExtra.GetClassName(h, sb, n);
    private static IntPtr GetParent(IntPtr h) => NativeMethodsExtra.GetParent(h);
    private static IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l)
        => NativeMethodsExtra.SendMessage(h, m, w, l);
    private static bool ScreenToClient(IntPtr h, ref NativeMethods.POINT p)
        => NativeMethods.ScreenToClient(h, ref p);
    private static bool ClientToScreen(IntPtr h, ref NativeMethods.POINT p)
        => NativeMethods.ClientToScreen(h, ref p);
}

/// <summary>サブフォルダメニューの1アイテム</summary>
public class SubFolderItem
{
    public string   Name           { get; set; } = "";
    public string   FullPath       { get; set; } = "";
    public bool     IsDirectory    { get; set; }
    public bool     HasChildren    { get; set; }
    public string   Extension      { get; set; } = "";
    public long     SizeBytes      { get; set; }
    public DateTime Modified       { get; set; }
    public bool     IsZip          { get; set; }
    public bool     IsArchiveEntry { get; set; }
    public string   ArchivePath    { get; set; } = "";

    public string DisplayName => Name;
    public string SizeDisplay => IsDirectory ? "" : FormatSize(SizeBytes);

    private static string FormatSize(long b)
    {
        if (b < 1024)             return $"{b} B";
        if (b < 1024 * 1024)     return $"{b / 1024.0:0.#} KB";
        if (b < 1024L*1024*1024) return $"{b / (1024.0*1024):0.#} MB";
        return $"{b / (1024.0*1024*1024):0.##} GB";
    }
}
