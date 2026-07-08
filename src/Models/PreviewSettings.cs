using System;
using System.Collections.Generic;

namespace QTBarExtension.Models;

/// <summary>
/// プレビュー機能の全設定。AppSettings に Preview プロパティとして追加する。
/// </summary>
public class PreviewSettings
{
    // ── 全般 ──────────────────────────────────────────────
    /// <summary>プレビュー機能全体の有効/無効</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>プレビューウィンドウのカラーテーマ: "system" / "dark" / "light"</summary>
    public string PreviewTheme { get; set; } = "system";

    /// <summary>プレビューが最初に表示されるまでの待機時間 (ms)</summary>
    public int HoverDelayMs { get; set; } = 150;

    /// <summary>プレビューウィンドウの不透明度 (0-100)</summary>
    public int OpacityPercent { get; set; } = 100;

    /// <summary>画像キャッシュの最大メモリサイズ (MiB)</summary>
    public int ImageCacheMaxMiB { get; set; } = 256;

    /// <summary>非アクティブウィンドウでもプレビューを表示するか</summary>
    public bool ShowWhenInactive { get; set; } = true;

    /// <summary>
    /// 画像プレビュー表示中にプレビューウィンドウ上にマウスが乗ったときにプレビューを解除し、
    /// その下にある別のファイルがあればプレビューし直すか（デフォルト: 有効）。
    /// 動画・フォルダ・圧縮フォルダのプレビューには影響しない。
    /// </summary>
    public bool HidePreviewOnHover { get; set; } = true;

    /// <summary>フォルダホバー時に中の最初のファイルをプレビューするか（通常フォルダ）</summary>
    public bool PreviewFolderContents { get; set; } = true;

    /// <summary>詳細表示ではフォルダ・圧縮フォルダをプレビューしない（デフォルト: 無効）</summary>
    public bool NoFolderPreviewInDetailView { get; set; } = false;

    /// <summary>ネットワークパス (UNC) のファイルもプレビュー対象にするか</summary>
    public bool PreviewNetworkFiles { get; set; } = true;

    /// <summary>圧縮フォルダ (zip等) 内のファイルもプレビュー対象にするか</summary>
    public bool PreviewArchiveContents { get; set; } = true;

    /// <summary>可能であればハードウェアアクセラレーションを使用する</summary>
    public bool UseHardwareAcceleration { get; set; } = true;

    /// <summary>動画・音声の再生位置を記録する</summary>
    public bool RememberPlaybackPosition { get; set; } = true;

    // ── 拡張子・フォント・色 ──────────────────────────────
    /// <summary>画像としてプレビューする拡張子（ドット込み小文字）</summary>
    public List<string> ImageExtensions { get; set; } =
    [
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".svg", ".heic"
    ];

    /// <summary>動画としてプレビューする拡張子</summary>
    public List<string> VideoExtensions { get; set; } =
    [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv"
    ];

    /// <summary>音声としてプレビューする拡張子</summary>
    public List<string> AudioExtensions { get; set; } =
    [
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma"
    ];

    /// <summary>テキストとしてプレビューする拡張子</summary>
    public List<string> TextExtensions { get; set; } =
    [
        ".txt", ".log", ".md", ".json", ".xml", ".csv", ".ini", ".cfg", ".cs", ".py", ".js",
        ".ts", ".html", ".css", ".yaml", ".yml", ".bat", ".ps1", ".sh"
    ];

    /// <summary>テキストプレビューのフォント名</summary>
    public string TextFontFamily { get; set; } = "Consolas";

    /// <summary>テキストプレビューのフォントサイズ</summary>
    public double TextFontSize { get; set; } = 14.0;

    /// <summary>テキストプレビューの文字色 (#RRGGBB)</summary>
    public string TextForegroundColor { get; set; } = "#E0E0E0";

    /// <summary>テキストプレビューの背景色 (#RRGGBB)</summary>
    public string TextBackgroundColor { get; set; } = "#202020";

    // ── プレビューウィンドウサイズ ────────────────────────
    /// <summary>画像プレビューの最大幅 (px)</summary>
    public int ImageMaxWidth { get; set; } = 640;

    /// <summary>画像プレビューの最大高さ (px)</summary>
    public int ImageMaxHeight { get; set; } = 384;

    /// <summary>動画プレビューの最大幅 (px)</summary>
    public int VideoMaxWidth { get; set; } = 640;

    /// <summary>動画プレビューの最大高さ (px)</summary>
    public int VideoMaxHeight { get; set; } = 384;

    /// <summary>テキストプレビューの最大幅 (px)</summary>
    public int TextMaxWidth { get; set; } = 512;

    /// <summary>テキストプレビューの最大高さ (px)</summary>
    public int TextMaxHeight { get; set; } = 256;

    /// <summary>テキストプレビューの最大文字数換算用フォントサイズ参考値 (px) ※互換用</summary>
    public int TextMaxFontSize { get; set; } = 256;

    /// <summary>テキストプレビューで読み込む先頭バイト数 (KiB)</summary>
    public int TextReadKiB { get; set; } = 1;

    /// <summary>動画・音声プレビューの再生音量 (0-100)</summary>
    public int PlaybackVolume { get; set; } = 50;

    /// <summary>圧縮フォルダとして扱う拡張子（ドット込み小文字）。SharpCompressで読み込み対応。</summary>
    public List<string> ArchiveExtensions { get; set; } =
    [
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".lzh", ".lza"
    ];

    // ── テキストプレビュー ────────────────────────────────
    /// <summary>テキストファイルのプレビューを表示するか（デフォルト: 有効）</summary>
    public bool PreviewTextFiles { get; set; } = true;

    /// <summary>
    /// テキストプレビュー表示中にプレビューウィンドウ上にマウスが乗ったときにプレビューを解除し、
    /// その下にある別のファイルがあればプレビューし直すか（デフォルト: 無効）。
    /// </summary>
    public bool HideTextPreviewOnHover { get; set; } = false;

    /// <summary>
    /// アニメーションWebPプレビュー表示中にプレビューウィンドウ上にマウスが乗ったときにプレビューを解除するか。
    /// アニメーション再生中に意図せず閉じないよう、デフォルトは無効。
    /// </summary>
    public bool HideAnimatedWebpOnHover { get; set; } = false;

    // ── デスクトップアイコンのプレビュー ─────────────────
    /// <summary>デスクトップアイコン上でもプレビューを有効にするか（デフォルト: 有効）</summary>
    public bool PreviewDesktopIcons { get; set; } = true;

    // ── 情報テキスト表示設定 ──────────────────────────────
    /// <summary>ファイルサイズを小数第2位まで表示するか（デフォルト: 有効）</summary>
    public bool ShowSizeWithTwoDecimals { get; set; } = true;

    /// <summary>情報テキストを複数行で表示するか（デフォルト: 有効）</summary>
    public bool InfoTextMultiLine { get; set; } = true;

    /// <summary>情報テキストに更新日時を表示するか（デフォルト: 有効）</summary>
    public bool InfoShowModifiedDate { get; set; } = true;

    /// <summary>情報テキストに画像/動画の解像度を表示するか（デフォルト: 有効）</summary>
    public bool InfoShowDimensions { get; set; } = true;

    /// <summary>情報テキストに動画の再生時間を表示するか（デフォルト: 有効）</summary>
    public bool InfoShowDuration { get; set; } = true;

    /// <summary>情報テキストに圧縮内/フォルダ内バッジを表示するか（デフォルト: 有効）</summary>
    public bool InfoShowLocationBadge { get; set; } = true;

    /// <summary>情報テキストにネットワークパスバッジを表示するか（デフォルト: 有効）</summary>
    public bool InfoShowNetworkBadge { get; set; } = true;

    /// <summary>情報テキストに作成日時を表示するか（デフォルト: 無効）</summary>
    public bool InfoShowCreatedDate { get; set; } = false;

    /// <summary>情報テキストに拡張子（種別）を表示するか（デフォルト: 無効）</summary>
    public bool InfoShowExtension { get; set; } = false;

    /// <summary>
    /// アニメーションWebPのFPS上書き設定。0以下のときはメタデータのフレーム遅延をそのまま使用。
    /// 正の値を指定するとすべてのフレームをこのFPSで再生する（メタデータを無視）。
    /// </summary>
    public double AnimatedWebpFpsOverride { get; set; } = 0;

    /// <summary>
    /// アニメーションWebPのFPS上書きを有効にするか（デフォルト: 無効）。
    /// 有効時は AnimatedWebpFpsOverride の値を使用してフレーム遅延を固定する。
    /// </summary>
    public bool AnimatedWebpFpsOverrideEnabled { get; set; } = false;

    /// <summary>
    /// 透過画像・アニメーション（WebP/GIF/APNG等）の透明部分に表示する背景。
    /// "tooltip": プレビューツールチップと同じ背景色（デフォルト）
    /// "checkerboard": 市松模様（透明グリッド）
    /// </summary>
    public string TransparencyBackground { get; set; } = "tooltip";

    // ── 再生位置の記憶用ストア（パス→秒） ─────────────────
    public Dictionary<string, double> PlaybackPositions { get; set; } = [];
}
