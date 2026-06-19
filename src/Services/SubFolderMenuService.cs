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
/// Win11最新Explorer対応版:
///   ・SysListView32 / UIItemsView / ItemsView などのHWND探索に依存しない。
///   ・カーソル下のウィンドウがCabinetWClass配下にあるかだけを確認し、
///     常にUIAutomationで直接要素を取得する方式に統一。
///   ・Explorer外では即座にチップを消す。
///   ・UIA非同期実行 + キャッシュでパフォーマンスを確保。
/// </summary>
public class SubFolderMenuService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly PreviewContentProvider _previewProvider;

    // ── 現在開いているメニュー ─────────────────────────────────
    private SubFolderMenuWindow? _currentMenu;
    private string? _currentMenuPath;
    private readonly HashSet<IntPtr> _menuHwnds = [];

    // ── フォルダビュー上チップ ────────────────────────────────
    private SubFolderChipWindow? _chip;
    private string?  _chipFolderPath;
    private IntPtr   _chipExplorerHwnd = IntPtr.Zero;

    // チップ表示タイマー（50ms ポーリング）
    private readonly DispatcherTimer _chipTimer;
    private NativeMethods.POINT _lastMousePt;
    private NativeMethods.POINT _stablePt;
    private int _stableCount;
    // 1回静止すれば即UIA実行（ほぼ遅延なし）
    private const int StableThreshold = 1;

    // UIA非同期実行フラグ
    private bool _chipUiaRunning;

    // UIA結果キャッシュ（同じ位置から8px以内は再実行しない）
    private NativeMethods.POINT _uiaCachedPt;
    private string?   _uiaCachedFolderPath;  // null = フォルダでない / "" = 未確定
    private bool      _uiaCacheValid;
    private double    _uiaCachedChipX;       // キャッシュ時のチップX座標
    private double    _uiaCachedChipY;       // キャッシュ時のチップY座標

    // 現在チップを表示しているアイテムのBoundingRect（ホバー範囲判定用）
    private Rect _currentItemBounds = Rect.Empty;

    // ── D&D 遅延タイマー ──────────────────────────────────────
    private readonly DispatcherTimer _dragTimer;
#pragma warning disable CS0649
    private string? _pendingDragPath;
    private NativeMethods.POINT _lastDragPt;
#pragma warning restore CS0649

    // ── マウスフック ──────────────────────────────────────────
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    // ── 公開イベント ──────────────────────────────────────────
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

        _chipTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _chipTimer.Tick += OnChipTimerTick;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        _hookProc = HookCallback;
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        using var mod  = proc.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc,
            GetModuleHandle(mod.ModuleName!), 0);

        _chipTimer.Start();
    }

    public void Stop()
    {
        _chipTimer.Stop();
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
            }
            else if ((msg == 0x0201 /*WM_LBUTTONDOWN*/ || msg == 0x0204 /*WM_RBUTTONDOWN*/)
                     && _currentMenu != null)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _dispatcher.InvokeAsync(() => CloseMenuIfClickedOutside(data.pt));
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void CloseMenuIfClickedOutside(NativeMethods.POINT pt)
    {
        if (_currentMenu == null) return;
        if (_chip != null && _chip.IsMouseOver) return;
        IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(pt);
        if (IsMenuWindowOrChild(hwndUnder)) return;
        CloseCurrentMenu();
    }

    private bool IsMenuWindowOrChild(IntPtr hwnd)
    {
        if (_currentMenu == null) return false;
        var cur = hwnd;
        for (int i = 0; i < 10 && cur != IntPtr.Zero; i++)
        {
            if (IsMenuHwnd(cur)) return true;
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return false;
    }

    private bool IsMenuHwnd(IntPtr hwnd)
    {
        if (_chip != null)
        {
            try
            {
                var chipHwnd = new System.Windows.Interop.WindowInteropHelper(_chip).Handle;
                if (hwnd == chipHwnd) return true;
            }
            catch { }
        }
        return _menuHwnds.Contains(hwnd);
    }

    private void RegisterMenuHwnd(SubFolderMenuWindow win)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(win);
            win.SourceInitialized += (_, _) =>
            {
                var h = helper.Handle;
                if (h != IntPtr.Zero) _menuHwnds.Add(h);
            };
            win.Closed += (_, _) =>
            {
                var h = helper.Handle;
                if (h != IntPtr.Zero) _menuHwnds.Remove(h);
            };
        }
        catch { }
    }

    // ── チップタイマー（50ms ポーリング）────────────────────────
    private void OnChipTimerTick(object? sender, EventArgs e)
    {
        if (!_settings.SubFolderMenu.Enabled)           { HideChip(); return; }
        if (!_settings.SubFolderMenu.ShowFolderViewChip){ HideChip(); return; }

        // チップ自体 or 開いているメニューにカーソルがある間は何もしない
        if (_chip != null && _chip.IsMouseOver) return;
        if (_currentMenu != null && _currentMenu.IsVisible) return;

        var pt = _lastMousePt;

        // カーソルが動いたかどうか
        bool moved = (pt.X != _stablePt.X || pt.Y != _stablePt.Y);

        if (moved)
        {
            _stablePt    = pt;
            _stableCount = 0;

            // チップが表示中なら「現在アイテムの範囲内か」で消すか判断
            if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver)
            {
                // BoundingRectが取得済みなら範囲チェック
                if (!_currentItemBounds.IsEmpty)
                {
                    bool inside = pt.X >= _currentItemBounds.Left &&
                                  pt.X <= _currentItemBounds.Right &&
                                  pt.Y >= _currentItemBounds.Top  &&
                                  pt.Y <= _currentItemBounds.Bottom;
                    if (!inside)
                    {
                        // アイテム外に出たら即消す・キャッシュ無効化
                        _uiaCacheValid = false;
                        _currentItemBounds = Rect.Empty;
                        HideChip();
                    }
                    // アイテム内なら消さない（return して静止カウントもしない）
                    return;
                }
                else
                {
                    // BoundingRect不明の場合は従来通り大きく動いたら消す
                    if (_chipFolderPath != null)
                    {
                        double dx = pt.X - _uiaCachedPt.X;
                        double dy = pt.Y - _uiaCachedPt.Y;
                        if (dx * dx + dy * dy > 30 * 30)
                        {
                            _uiaCacheValid = false;
                            HideChip();
                        }
                    }
                    return;
                }
            }

            // チップ非表示中はキャッシュ無効化のみ
            _uiaCacheValid = false;
            return;
        }

        // カーソル静止
        _stableCount++;
        if (_stableCount < StableThreshold) return;
        if (_stableCount > StableThreshold) return; // 1回だけ実行

        // ── カーソル下がCabinetWClass配下かチェック ──────────────
        IntPtr hwndUnder = NativeMethodsExtra.WindowFromPoint(pt);
        IntPtr cabinet   = FindCabinetAncestor(hwndUnder);

        if (cabinet == IntPtr.Zero)
        {
            if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver)
                HideChip();
            return;
        }

        // 非アクティブ抑制
        if (!_settings.SubFolderMenu.ShowWhenInactive)
        {
            IntPtr fg = GetForegroundWindow();
            if (!IsSameTopLevel(hwndUnder, fg)) { HideChip(); return; }
        }

        // キャッシュが有効で同じ位置付近（8px以内）なら再実行しない
        if (_uiaCacheValid)
        {
            int dx2 = pt.X - _uiaCachedPt.X;
            int dy2 = pt.Y - _uiaCachedPt.Y;
            if (dx2 * dx2 + dy2 * dy2 <= 8 * 8)
            {
                if (_uiaCachedFolderPath != null && _chipFolderPath != _uiaCachedFolderPath)
                    ShowChip(_uiaCachedChipX, _uiaCachedChipY,
                             _uiaCachedFolderPath, cabinet);
                return;
            }
        }

        // UIA実行中なら skip
        if (_chipUiaRunning) return;
        _chipUiaRunning = true;

        var capturedPt      = pt;
        var capturedCabinet = cabinet;

        Task.Run(() =>
        {
            string? itemName      = null;
            Rect?   itemBounds    = null;
            double? nameColumnRight = null;
            try
            {
                var element = AutomationElement.FromPoint(
                    new System.Windows.Point(capturedPt.X, capturedPt.Y));
                if (element != null)
                {
                    var walker = TreeWalker.ControlViewWalker;
                    AutomationElement? cur = element;
                    for (int depth = 0; depth < 10 && cur != null; depth++)
                    {
                        var ct = cur.GetCurrentPropertyValue(
                            AutomationElementIdentifiers.ControlTypeProperty) as ControlType;

                        if (ct == ControlType.ListItem || ct == ControlType.DataItem ||
                            ct == ControlType.TreeItem)
                        {
                            var name = cur.GetCurrentPropertyValue(
                                AutomationElementIdentifiers.NameProperty) as string;
                            if (!string.IsNullOrEmpty(name))
                            {
                                itemName = name.Split('\n')[0].Trim();
                                var bounds = cur.GetCurrentPropertyValue(
                                    AutomationElementIdentifiers.BoundingRectangleProperty);
                                if (bounds is Rect r && r.Width > 0)
                                {
                                    itemBounds = r;

                                    if (r.Height <= 30)
                                    {
                                        try
                                        {
                                            var firstChild = walker.GetFirstChild(cur);
                                            if (firstChild != null)
                                            {
                                                var cb = firstChild.GetCurrentPropertyValue(
                                                    AutomationElementIdentifiers.BoundingRectangleProperty);
                                                if (cb is Rect cr && cr.Width > 0)
                                                    nameColumnRight = cr.Right;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            break;
                        }
                        if (ct == ControlType.List   || ct == ControlType.Pane   ||
                            ct == ControlType.Window || ct == ControlType.ScrollBar) break;
                        cur = walker.GetParent(cur);
                    }
                }
            }
            catch { }

            var capturedBounds = itemBounds;
            _dispatcher.InvokeAsync(() =>
            {
                _chipUiaRunning = false;
                _uiaCachedPt    = capturedPt;
                _uiaCacheValid  = true;

                if (string.IsNullOrEmpty(itemName))
                {
                    _uiaCachedFolderPath = null;
                    _currentItemBounds   = Rect.Empty;
                    if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver) HideChip();
                    return;
                }

                string? folderPath = GetFolderPathForCabinet(capturedCabinet);
                if (string.IsNullOrEmpty(folderPath))
                {
                    _uiaCachedFolderPath = null;
                    _currentItemBounds   = Rect.Empty;
                    HideChip();
                    return;
                }

                string fullPath = ResolveFullPath(folderPath, itemName);

                bool isFolder = Directory.Exists(fullPath);
                bool isZip    = !isFolder && IsZipFile(fullPath)
                                && _settings.SubFolderMenu.EnableForZip;

                if (!isFolder && !isZip)
                {
                    _uiaCachedFolderPath = null;
                    _currentItemBounds   = Rect.Empty;
                    if (_chip != null && _chip.IsVisible && !_chip.IsMouseOver) HideChip();
                    return;
                }

                _uiaCachedFolderPath = fullPath;
                // BoundingRectを保存（ホバー範囲判定に使う）
                _currentItemBounds = capturedBounds ?? Rect.Empty;

                // 既に同じフォルダのチップが表示中なら何もしない
                if (_chipFolderPath == fullPath && _chip != null && _chip.IsVisible) return;

                // チップ表示位置を計算
                const double ChipSize = 15.0;
                double chipX, chipY;
                if (itemBounds.HasValue)
                {
                    double itemH = itemBounds.Value.Height;
                    double itemW = itemBounds.Value.Width;

                    if (itemH <= 30)
                    {
                        double nameRight;
                        if (nameColumnRight.HasValue)
                            nameRight = nameColumnRight.Value - 1;
                        else
                            nameRight = Math.Min(capturedPt.X + 40, itemBounds.Value.Right - ChipSize - 2);
                        chipX = nameRight - ChipSize;
                        chipY = itemBounds.Value.Top + (itemH - ChipSize) / 2.0;
                    }
                    else
                    {
                        double iconAreaH = Math.Min(itemH * 0.65, 96);
                        double iconAreaW = Math.Min(iconAreaH, itemW);
                        double iconLeft  = itemBounds.Value.Left + (itemW - iconAreaW) / 2.0;
                        double iconRight = iconLeft + iconAreaW;
                        double iconBottom = itemBounds.Value.Top + iconAreaH;

                        chipX = iconRight  - ChipSize - 1;
                        chipY = iconBottom - ChipSize - 1;
                    }
                }
                else
                {
                    chipX = capturedPt.X + 16;
                    chipY = capturedPt.Y - 7;
                }

                _chipFolderPath   = fullPath;
                _uiaCachedChipX   = chipX;
                _uiaCachedChipY   = chipY;
                ShowChip(chipX, chipY, fullPath, capturedCabinet);
            });
        });
    }

    // ── ユーティリティ ────────────────────────────────────────
    private static string ResolveFullPath(string folderPath, string itemName)
    {
        string direct = Path.Combine(folderPath, itemName);
        if (Directory.Exists(direct) || File.Exists(direct)) return direct;

        // 拡張子なし → 同名ファイル/フォルダを検索
        try
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(itemName);
            if (string.Equals(itemName, nameNoExt, StringComparison.Ordinal))
            {
                var fileCandidates = Directory.GetFiles(folderPath, itemName + ".*");
                if (fileCandidates.Length > 0) return fileCandidates[0];
                var dirCandidates  = Directory.GetDirectories(folderPath, itemName + "*");
                if (dirCandidates.Length  > 0) return dirCandidates[0];
            }
        }
        catch { }
        return direct;
    }

    private static bool IsZipFile(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    // ── チップ表示/非表示 ────────────────────────────────────
    private void ShowChip(double screenX, double screenY, string folderPath,
                          IntPtr explorerHwnd = default)
    {
        _chipExplorerHwnd = explorerHwnd;
        if (_chip == null)
        {
            _chip = new SubFolderChipWindow();
            _chip.ChipClicked += (path, x, y) =>
                OpenMenuAt(path, x, y, _chipExplorerHwnd);
        }
        _chip.ShowAt(screenX, screenY, folderPath);
    }

    private void HideChip()
    {
        _chipFolderPath    = null;
        _chipExplorerHwnd  = IntPtr.Zero;
        _currentItemBounds = Rect.Empty;
        _chip?.HideChip();
    }

    // ── タブアイコン クリック ────────────────────────────────
    public void ShowForTabIcon(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (!_settings.SubFolderMenu.EnableTabIconSubFolder) return;
        if (string.IsNullOrEmpty(folderPath)) return;
        OpenMenuAt(folderPath, screenX, screenY, explorerHwnd);
    }

    public void ShowParentForTabIcon(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (!_settings.SubFolderMenu.EnableTabIconSubFolder) return;
        string? parent = Path.GetDirectoryName(folderPath.TrimEnd('\\', '/'));
        if (parent == null || parent == folderPath) return;
        OpenMenuAt(parent, screenX, screenY, explorerHwnd);
    }

    public void ShowForTab(string folderPath, double screenX, double screenY,
        IntPtr explorerHwnd)
    {
        if (!_settings.SubFolderMenu.Enabled) return;
        if (string.IsNullOrEmpty(folderPath)) return;
        if (folderPath == _currentMenuPath && _currentMenu?.IsVisible == true) return;
        OpenMenuAt(folderPath, screenX, screenY, explorerHwnd);
    }

    public void CloseMenu() => CloseCurrentMenu();

    // ── メニュー生成 ─────────────────────────────────────────
    private void OpenMenuAt(string rootPath, double screenX, double screenY, IntPtr explorerHwnd)
    {
        CloseCurrentMenu();
        _currentMenuPath = rootPath;

        Action<string> navigateAction = path =>
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
        };

        Task.Run(() =>
        {
            var items = LoadItems(rootPath);
            _dispatcher.InvokeAsync(() =>
            {
                if (_currentMenuPath != rootPath) return;
                _currentMenu = CreateMenuWindow(items, rootPath, navigateAction,
                                                explorerHwnd, isChild: false);
                _currentMenu.Closed += (_, _) =>
                {
                    _currentMenu     = null;
                    _currentMenuPath = null;
                    MenuStateChanged?.Invoke(null);
                };
                _currentMenu.ShowAt(screenX, screenY);
                MenuStateChanged?.Invoke(rootPath);
            });
        });
    }

    private void OpenSubMenuAt(SubFolderMenuWindow parentMenu, string rootPath,
        double screenX, double screenY, IntPtr explorerHwnd, Action<string> navigateAction)
    {
        Task.Run(() =>
        {
            var items = LoadItems(rootPath);
            _dispatcher.InvokeAsync(() =>
            {
                var child = CreateMenuWindow(items, rootPath, navigateAction,
                                             explorerHwnd, isChild: true);
                parentMenu.SetChildMenu(child);
                child.ShowAt(screenX, screenY);
            });
        });
    }

    private SubFolderMenuWindow CreateMenuWindow(
        List<SubFolderItem> items, string rootPath,
        Action<string> navigateAction, IntPtr explorerHwnd, bool isChild)
    {
        SubFolderMenuWindow? win = null;
        win = new SubFolderMenuWindow(
            items, rootPath, _settings.SubFolderMenu, _previewProvider,
            onNavigate: navigateAction,
            onOpenSubmenu: (path, x, y) =>
            {
                if (win != null)
                    OpenSubMenuAt(win, path, x, y, explorerHwnd, navigateAction);
            }
        );
        RegisterMenuHwnd(win);
        return win;
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

    /// <summary>
    /// カーソル下のHWNDから親を遡ってCabinetWClassを探す。
    /// Win11のExplorerはウィンドウ階層が環境によって異なるため、
    /// SysListView32等の特定クラスに依存せずCabinetWClassのみで判定する。
    /// </summary>
    private static IntPtr FindCabinetAncestor(IntPtr hwnd)
    {
        var cur = hwnd;
        for (int i = 0; i < 20 && cur != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(64);
            NativeMethodsExtra.GetClassName(cur, sb, 64);
            string cls = sb.ToString();
            if (cls == "CabinetWClass") return cur;
            // デスクトップ/シェルルートに達したら終了
            if (cls == "WorkerW" || cls == "Progman" || cls == "Shell_TrayWnd") break;
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return IntPtr.Zero;
    }

    private static string? GetFolderPathForCabinet(IntPtr cabinet)
    {
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
            cur = NativeMethodsExtra.GetParent(cur);
        }
        return false;
    }

    private void CloseCurrentMenu()
    {
        try { _currentMenu?.CloseAll(); } catch { }
        _currentMenu     = null;
        _currentMenuPath = null;
    }

    // NativeMethods エイリアス
    private static IntPtr GetForegroundWindow() => NativeMethodsExtra.GetForegroundWindow();
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
