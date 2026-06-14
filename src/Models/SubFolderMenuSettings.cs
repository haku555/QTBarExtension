using System;
using System.Windows.Media;

namespace QTBarExtension.Models;

/// <summary>サブフォルダメニューのソート方法</summary>
public enum SubFolderSortMode
{
    Extension,      // 拡張子（デフォルト）
    Name,           // 名前
    ModifiedDesc,   // 更新日時（新しいのが上）
    ModifiedAsc,    // 更新日時（古いのが上）
    None,           // なし（ファイルシステム順）
}

/// <summary>サブフォルダメニューの全設定</summary>
public class SubFolderMenuSettings
{
    /// <summary>サブフォルダメニュー機能を有効にする</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ファイルを表示する</summary>
    public bool ShowFiles { get; set; } = true;

    /// <summary>隠し属性のオブジェクトを表示する</summary>
    public bool ShowHidden { get; set; } = true;

    /// <summary>システム属性のオブジェクトを表示する</summary>
    public bool ShowSystem { get; set; } = true;

    /// <summary>ツールチップでプレビューする</summary>
    public bool TooltipPreview { get; set; } = true;

    /// <summary>フォルダウィンドウが非アクティブでも表示する</summary>
    public bool ShowWhenInactive { get; set; } = true;

    /// <summary>グループファイル（.library-ms等）はルートに展開する</summary>
    public bool ExpandGroupFiles { get; set; } = true;

    /// <summary>ドラッグ＆ドロップ中にマウス下のフォルダのメニューを表示する</summary>
    public bool ShowOnDragOver { get; set; } = true;

    /// <summary>圧縮フォルダ（zip等）に対してもメニューを有効にする</summary>
    public bool EnableForZip { get; set; } = true;

    /// <summary>ライブラリはフォルダごとにソートする</summary>
    public bool SortLibraryByFolder { get; set; } = true;

    /// <summary>カスタムカラーの設定を使用する</summary>
    public bool UseCustomColors { get; set; } = true;

    /// <summary>カスタム背景色 (#AARRGGBB)</summary>
    public string BackgroundColor { get; set; } = "#FF2D2D2D";

    /// <summary>カスタム前景色（フォルダ）(#AARRGGBB)</summary>
    public string FolderColor { get; set; } = "#FF7EB8FF";

    /// <summary>カスタム前景色（ファイル）(#AARRGGBB)</summary>
    public string FileColor { get; set; } = "#FFE0E0E0";

    /// <summary>ハイライト色 (#AARRGGBB)</summary>
    public string HighlightColor { get; set; } = "#FF0078D4";

    /// <summary>アイテムのソート方法</summary>
    public SubFolderSortMode SortMode { get; set; } = SubFolderSortMode.Extension;

    /// <summary>メニューの不透明度 (0-100)</summary>
    public int OpacityPercent { get; set; } = 100;

    /// <summary>ファイルドラッグでメニューが表示されるまでの時間 (ms)</summary>
    public int DragOpenDelayMs { get; set; } = 1200;

    /// <summary>プレビューがマウスによって表示されるまでの時間 (ms)</summary>
    public int PreviewDelayMs { get; set; } = 0;

    /// <summary>タブ上でサブフォルダチップを有効にする（タブのアイコンクリックで展開）</summary>
    public bool EnableTabIconSubFolder { get; set; } = true;

    /// <summary>サブフォルダチップを表示する（フォルダビュー上）</summary>
    public bool ShowFolderViewChip { get; set; } = true;

    /// <summary>メニューの最大表示アイテム数（これを超えたら「さらに表示」）</summary>
    public int MaxItemsPerMenu { get; set; } = 40;

    /// <summary>フォントサイズ</summary>
    public double FontSize { get; set; } = 12.0;
}
