using System;
using System.Runtime.InteropServices;

namespace QTBarExtension.Core;

/// <summary>
/// .lnk ショートカットファイルを作成するヘルパー。
/// 専用NuGetパッケージを追加せず、WScript.Shell COMオブジェクトを
/// 動的に呼び出すことで実現する（Windows標準搭載のため依存関係なし）。
/// </summary>
internal static class ShortcutHelper
{
    /// <summary>
    /// ショートカット(.lnk)を作成する。
    /// </summary>
    /// <param name="shortcutPath">作成先の .lnk フルパス</param>
    /// <param name="targetPath">リンク先の実行ファイルパス</param>
    /// <param name="workingDir">作業ディレクトリ</param>
    /// <param name="iconPath">アイコンファイルパス（.ico）。空の場合はtargetPathのアイコンを使用</param>
    /// <param name="description">ショートカットの説明文</param>
    public static void CreateShortcut(string shortcutPath, string targetPath, string workingDir,
        string? iconPath = null, string? description = null)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COMオブジェクトを取得できませんでした。");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("WScript.Shellのインスタンス化に失敗しました。");

            shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                ?? throw new InvalidOperationException("CreateShortcut呼び出しに失敗しました。");

            Type shortcutType = shortcut.GetType();

            shortcutType.InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [workingDir]);

            if (!string.IsNullOrEmpty(description))
            {
                shortcutType.InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, [description]);
            }

            if (!string.IsNullOrEmpty(iconPath) && System.IO.File.Exists(iconPath))
            {
                shortcutType.InvokeMember("IconLocation",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{iconPath}, 0"]);
            }

            shortcutType.InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.ReleaseComObject(shortcut);
            if (shell != null)    Marshal.ReleaseComObject(shell);
        }
    }
}
