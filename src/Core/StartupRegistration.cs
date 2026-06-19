using System;
using Microsoft.Win32;

namespace QTBarExtension.Core;

/// <summary>
/// Windows起動時の自動実行（スタートアップ登録）を管理する。
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run にエントリを追加/削除する方式。
/// 管理者権限不要、ユーザー単位での登録。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "QTBarExtension";

    /// <summary>
    /// 現在自動実行が有効かどうかを返す。
    /// 登録されているパスが現在の実行ファイルと異なる場合もfalseを返す
    /// （別の場所に配置し直した場合に古いエントリを誤って「有効」と判定しないため）。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var existing = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(existing)) return false;

            string current = GetQuotedExePath();
            return string.Equals(existing, current, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("レジストリキーを開けませんでした。");
        key.SetValue(ValueName, GetQuotedExePath(), RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) != null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetQuotedExePath()
    {
        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("実行ファイルパスを取得できませんでした。");
        return $"\"{exePath}\"";
    }
}
