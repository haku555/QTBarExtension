using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QTBarExtension.Services;

/// <summary>
/// Shell.Application COMオブジェクトへのアクセスを安全にラップ。
/// 呼び出し元は必ずUIスレッド(Dispatcher)から呼ぶこと。
/// </summary>
public static class ShellHelper
{
    public record ExplorerTab(IntPtr Hwnd, string LocationURL);

    public static List<ExplorerTab> GetAllExplorerTabs()
    {
        var result = new List<ExplorerTab>();
        object? shellObj = null;
        dynamic? windows  = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return result;
            shellObj = Activator.CreateInstance(shellType);
            if (shellObj == null) return result;

            dynamic shell = shellObj;
            windows = shell.Windows();
            int count = (int)windows.Count;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? win = windows.Item(i);
                    if (win == null) continue;
                    IntPtr hwnd = (IntPtr)(int)win.HWND;
                    if (hwnd == IntPtr.Zero) continue;
                    string url = "";
                    try { url = (string?)win.LocationURL ?? ""; } catch { }
                    result.Add(new ExplorerTab(hwnd, url));
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            try { if (windows  != null) Marshal.ReleaseComObject(windows);  } catch { }
            try { if (shellObj != null) Marshal.ReleaseComObject(shellObj); } catch { }
        }
        return result;
    }

    public static bool NavigateTo(IntPtr explorerHwnd, string localPath)
    {
        object? shellObj = null;
        dynamic? windows  = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;
            shellObj = Activator.CreateInstance(shellType);
            if (shellObj == null) return false;

            dynamic shell = shellObj;
            windows = shell.Windows();
            int count = (int)windows.Count;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? win = windows.Item(i);
                    if (win == null) continue;
                    IntPtr hwnd = (IntPtr)(int)win.HWND;
                    if (hwnd != explorerHwnd) continue;
                    win.Navigate(localPath);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            try { if (windows  != null) Marshal.ReleaseComObject(windows);  } catch { }
            try { if (shellObj != null) Marshal.ReleaseComObject(shellObj); } catch { }
        }
        return false;
    }

    public static string UrlToLocalPath(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Uri.UnescapeDataString(
                    url.Replace("file:///", "", StringComparison.OrdinalIgnoreCase)
                       .Replace('/', '\\'));
            }
            catch { }
        }
        return url;
    }

    public static string GetDisplayName(string pathOrUrl)
    {
        string local = UrlToLocalPath(pathOrUrl);
        string name  = System.IO.Path.GetFileName(local.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? local : name;
    }
}
