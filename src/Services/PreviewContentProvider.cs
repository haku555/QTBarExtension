using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using QTBarExtension.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace QTBarExtension.Services;

public enum PreviewKind { None, Image, Video, Audio, Text, Unsupported }

public class PreviewInfo
{
    public PreviewKind Kind { get; set; }
    public string DisplayPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? Created { get; set; }
    public string Dimensions { get; set; } = "";   // 画像/動画用
    public TimeSpan? Duration { get; set; }          // 動画/音声用
    public string TextContent { get; set; } = "";   // テキスト用
    public BitmapImage? Image { get; set; }
    public string? TempMediaPath { get; set; }       // アーカイブ内ファイルを展開した一時パス
    public bool IsArchiveEntry { get; set; }
    public bool IsNetworkPath { get; set; }
    public bool IsFolderItem { get; set; }           // フォルダ/zip内ナビゲーション由来

    // ── フォルダ内ナビゲーション用 ──────────────────────────
    /// <summary>同フォルダ/zip内でプレビュー可能なファイルパスの一覧（null=ナビ不要）</summary>
    public List<string>? FolderItems { get; set; }
    /// <summary>FolderItems 内の現在インデックス</summary>
    public int FolderIndex { get; set; }
}

/// <summary>
/// プレビュー対象ファイルの情報を取得・デコードするサービス。
/// 画像はメモリキャッシュ(LRU, 設定の最大MiBまで)を持つ。
/// アーカイブ(zip)内ファイルは一時フォルダに展開してプレビューする。
/// フォルダ/zip ホバー時は内部の最初のプレビュー可能ファイルを返す。
/// </summary>
public class PreviewContentProvider
{
    private readonly PreviewSettings _settings;

    /// <summary>設定への参照（SubFolderMenuWindowのPreviewTooltip等で利用）</summary>
    public PreviewSettings Settings => _settings;

    // 画像キャッシュ: パス(またはzip!entry) -> (BitmapImage, byte概算サイズ)
    private readonly Dictionary<string, (BitmapImage Image, long Size)> _imageCache = new();
    private readonly LinkedList<string> _lru = new();
    private long _cacheBytes;

    /// <summary>現在の画像キャッシュ使用量（バイト）</summary>
    public long CacheUsageBytes => _cacheBytes;

    /// <summary>現在のキャッシュエントリ数</summary>
    public int CacheEntryCount => _imageCache.Count;

    private static readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "QTBarExtension_Preview");

    public PreviewContentProvider(PreviewSettings settings)
    {
        _settings = settings;
        try { Directory.CreateDirectory(_tempDir); } catch { }
    }

    public void ClearCache()
    {
        _imageCache.Clear();
        _lru.Clear();
        _cacheBytes = 0;
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var f in Directory.GetFiles(_tempDir))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    // ── フォルダ種別判定 ─────────────────────────────────────────────

    /// <summary>通常フォルダかどうか（ファイルでもzipでもない）</summary>
    public static bool IsDirectory(string path) =>
        !string.IsNullOrEmpty(path) && Directory.Exists(path) && !File.Exists(path);

    /// <summary>zipファイル自体（内部エントリではない）かどうか ※互換用</summary>
    public static bool IsZipFile(string path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) &&
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>圧縮フォルダファイル自体（内部エントリではない）かどうか。設定の拡張子リストで判定。</summary>
    public bool IsArchiveFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // ".tar.gz" のような複合拡張子にも対応
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            return _settings.ArchiveExtensions.Contains(".tar") ||
                   _settings.ArchiveExtensions.Contains(ext);
        return _settings.ArchiveExtensions.Contains(ext);
    }

    // ── フォルダ内コンテンツ列挙 ────────────────────────────────────

    /// <summary>
    /// 通常フォルダ内のプレビュー可能ファイルを列挙する（画像・動画のみ、名前順）。
    /// GetKind は設定フラグで結果が変わるため、拡張子チェックを直接行う。
    /// </summary>
    public List<string> GetFolderPreviewItems(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return [];
        try
        {
            return Directory.GetFiles(folderPath)
                .Where(f => KindFromExtension(Path.GetExtension(f))
                            is PreviewKind.Image or PreviewKind.Video)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// 圧縮フォルダ内のプレビュー可能エントリを列挙する（画像・動画のみ、名前順）。
    /// zip以外（rar/7z/tar/gz等）も SharpCompress 経由で対応。
    /// 返すパスは "archivePath\entry\path" 形式（Load で TryGetArchiveParts が解析できる形）。
    /// </summary>
    public List<string> GetZipPreviewItems(string zipPath) => GetArchivePreviewItems(zipPath);

    public List<string> GetArchivePreviewItems(string archivePath)
    {
        if (!File.Exists(archivePath)) return [];
        try
        {
            if (IsZipFile(archivePath))
            {
                using var zip = ZipFile.OpenRead(archivePath);
                return zip.Entries
                    .Where(e => !e.FullName.EndsWith('/') &&
                                KindFromExtension(Path.GetExtension(e.FullName))
                                is PreviewKind.Image or PreviewKind.Video)
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .Select(e => archivePath + "\\" + e.FullName.Replace('/', '\\'))
                    .ToList();
            }

            using var archive = ArchiveFactory.OpenArchive(archivePath);
            return archive.Entries
                .Where(e => !e.IsDirectory &&
                            KindFromExtension(Path.GetExtension(e.Key ?? ""))
                            is PreviewKind.Image or PreviewKind.Video)
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => archivePath + "\\" + (e.Key ?? "").Replace('/', '\\'))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// パスがプレビュー対象かどうか判定する。
    /// 通常パス・UNCパス・"archive.zip\entry\path" 形式（圧縮フォルダ内）に対応。
    /// フォルダ・zipファイル自体は None を返す（GetKindForFolderEntry で別処理）。
    /// </summary>
    public PreviewKind GetKind(string path)
    {
        if (string.IsNullOrEmpty(path)) return PreviewKind.None;
        if (!_settings.Enabled) return PreviewKind.None;

        // フォルダ・zipそのものはここでは None（HoverService側で判別）
        if (IsDirectory(path)) return PreviewKind.None;

        if (IsNetworkPath(path) && !_settings.PreviewNetworkFiles) return PreviewKind.None;

        if (TryGetArchiveParts(path, out _, out string entry))
        {
            if (!_settings.PreviewArchiveContents) return PreviewKind.None;
            return KindFromExtension(Path.GetExtension(entry));
        }

        return KindFromExtension(Path.GetExtension(path));
    }

    private PreviewKind KindFromExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (_settings.ImageExtensions.Contains(ext)) return PreviewKind.Image;
        if (_settings.VideoExtensions.Contains(ext)) return PreviewKind.Video;
        if (_settings.AudioExtensions.Contains(ext)) return PreviewKind.Audio;
        if (_settings.TextExtensions.Contains(ext)) return PreviewKind.Text;
        return PreviewKind.Unsupported;
    }

    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// "C:\folder\archive.zip\sub\file.png" のような結合パスを
    /// (archivePath, internalEntryPath) に分解する。圧縮フォルダでなければ false。
    /// zip以外（rar/7z/tar/gz等、設定の ArchiveExtensions に含まれる拡張子）にも対応。
    /// </summary>
    public bool TryGetArchiveParts(string path, out string archivePath, out string entryPath)
    {
        archivePath = ""; entryPath = "";

        foreach (var ext in _settings.ArchiveExtensions)
        {
            var idx = path.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int splitAt = idx + ext.Length;
            if (splitAt >= path.Length) continue;
            if (path[splitAt] != '\\' && path[splitAt] != '/') continue;

            string candidate = path[..splitAt];
            string entry = path[(splitAt + 1)..].Replace('\\', '/');
            if (File.Exists(candidate) && entry.Length > 0)
            {
                archivePath = candidate;
                entryPath = entry;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// プレビュー情報を取得する。重い処理を含むためバックグラウンドスレッドから呼ぶこと。
    /// folderItems が非null の場合、フォルダナビゲーション情報を PreviewInfo に付加する。
    /// </summary>
    public PreviewInfo? Load(string path,
        List<string>? folderItems = null, int folderIndex = 0)
    {
        var kind = GetKind(path);
        if (kind == PreviewKind.None || kind == PreviewKind.Unsupported) return null;

        bool isArchive = TryGetArchiveParts(path, out string zipPath, out string entryPath);
        bool isNetwork = IsNetworkPath(path);

        var info = new PreviewInfo
        {
            Kind = kind,
            DisplayPath = path,
            FileName = isArchive ? Path.GetFileName(entryPath) : Path.GetFileName(path),
            IsArchiveEntry = isArchive,
            IsNetworkPath = isNetwork,
            FolderItems = folderItems,
            FolderIndex = folderIndex,
            IsFolderItem = folderItems != null,
        };

        try
        {
            if (isArchive)
            {
                LoadFromArchive(info, zipPath, entryPath);
            }
            else
            {
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                info.SizeBytes = fi.Length;
                info.Modified = fi.LastWriteTime;
                info.Created = fi.CreationTime;

                switch (kind)
                {
                    case PreviewKind.Image:
                        info.Image = LoadImage(path, path);
                        if (info.Image != null)
                            info.Dimensions = $"{info.Image.PixelWidth} x {info.Image.PixelHeight}";
                        break;
                    case PreviewKind.Text:
                        info.TextContent = LoadTextHead(path);
                        break;
                    case PreviewKind.Video:
                    case PreviewKind.Audio:
                        info.TempMediaPath = path;
                        break;
                }
            }
        }
        catch
        {
            return null;
        }

        return info;
    }

    private void LoadFromArchive(PreviewInfo info, string archivePath, string entryPath)
    {
        if (IsZipFile(archivePath))
        {
            LoadFromZip(info, archivePath, entryPath);
            return;
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entry = archive.Entries.FirstOrDefault(e =>
            !e.IsDirectory &&
            (e.Key ?? "").Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        info.SizeBytes = entry.Size;
        info.Modified = entry.LastModifiedTime;

        string cacheKey = archivePath + "!" + entry.Key;

        switch (info.Kind)
        {
            case PreviewKind.Image:
                if (TryGetCachedImage(cacheKey, out var cached))
                {
                    info.Image = cached;
                }
                else
                {
                    using var stream = entry.OpenEntryStream();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    var bmp = DecodeImage(ms);
                    if (bmp != null)
                    {
                        info.Image = bmp;
                        AddToCache(cacheKey, bmp, ms.Length);
                    }
                }
                if (info.Image != null)
                    info.Dimensions = $"{info.Image.PixelWidth} x {info.Image.PixelHeight}";
                break;

            case PreviewKind.Text:
                using (var stream = entry.OpenEntryStream())
                {
                    info.TextContent = ReadTextHead(stream);
                }
                break;

            case PreviewKind.Video:
            case PreviewKind.Audio:
                string tempPath = Path.Combine(_tempDir,
                    SanitizeFileName(archivePath.GetHashCode().ToString("X") + "_" + Path.GetFileName(entry.Key)));
                if (!File.Exists(tempPath))
                {
                    using var stream = entry.OpenEntryStream();
                    using var fs = File.Create(tempPath);
                    stream.CopyTo(fs);
                }
                info.TempMediaPath = tempPath;
                break;
        }
    }

    private void LoadFromZip(PreviewInfo info, string zipPath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryPath) ??
                    zip.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;

        info.SizeBytes = entry.Length;
        info.Modified = entry.LastWriteTime.DateTime;

        string cacheKey = zipPath + "!" + entry.FullName;

        switch (info.Kind)
        {
            case PreviewKind.Image:
                if (TryGetCachedImage(cacheKey, out var cached))
                {
                    info.Image = cached;
                }
                else
                {
                    using var stream = entry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;
                    var bmp = DecodeImage(ms);
                    if (bmp != null)
                    {
                        info.Image = bmp;
                        AddToCache(cacheKey, bmp, ms.Length);
                    }
                }
                if (info.Image != null)
                    info.Dimensions = $"{info.Image.PixelWidth} x {info.Image.PixelHeight}";
                break;

            case PreviewKind.Text:
                using (var stream = entry.Open())
                {
                    info.TextContent = ReadTextHead(stream);
                }
                break;

            case PreviewKind.Video:
            case PreviewKind.Audio:
                string tempPath = Path.Combine(_tempDir,
                    SanitizeFileName(zipPath.GetHashCode().ToString("X") + "_" + entry.Name));
                if (!File.Exists(tempPath))
                {
                    entry.ExtractToFile(tempPath, overwrite: true);
                }
                info.TempMediaPath = tempPath;
                break;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── 画像読み込み ────────────────────────────────────────
    private BitmapImage? LoadImage(string path, string cacheKey)
    {
        if (TryGetCachedImage(cacheKey, out var cached)) return cached;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (!_settings.UseHardwareAcceleration)
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        int maxDim = Math.Max(_settings.ImageMaxWidth, _settings.ImageMaxHeight) * 2;
        bmp.DecodePixelWidth = maxDim;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        long sizeEstimate = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        AddToCache(cacheKey, bmp, sizeEstimate);
        return bmp;
    }

    private BitmapImage? DecodeImage(Stream stream)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            int maxDim = Math.Max(_settings.ImageMaxWidth, _settings.ImageMaxHeight) * 2;
            bmp.DecodePixelWidth = maxDim;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ── キャッシュ管理 ─────────────────────────────────────
    private bool TryGetCachedImage(string key, out BitmapImage image)
    {
        if (_imageCache.TryGetValue(key, out var entry))
        {
            _lru.Remove(key);
            _lru.AddFirst(key);
            image = entry.Image;
            return true;
        }
        image = null!;
        return false;
    }

    private void AddToCache(string key, BitmapImage image, long sizeBytes)
    {
        long maxBytes = (long)_settings.ImageCacheMaxMiB * 1024 * 1024;
        if (sizeBytes > maxBytes) return;

        if (_imageCache.ContainsKey(key))
        {
            _cacheBytes -= _imageCache[key].Size;
            _lru.Remove(key);
        }

        _imageCache[key] = (image, sizeBytes);
        _lru.AddFirst(key);
        _cacheBytes += sizeBytes;

        while (_cacheBytes > maxBytes && _lru.Count > 0)
        {
            var lastKey = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_imageCache.TryGetValue(lastKey, out var old))
            {
                _cacheBytes -= old.Size;
                _imageCache.Remove(lastKey);
            }
        }
    }

    // ── テキスト読み込み ────────────────────────────────────
    private string LoadTextHead(string path)
    {
        using var fs = File.OpenRead(path);
        return ReadTextHead(fs);
    }

    private string ReadTextHead(Stream stream)
    {
        int maxBytes = Math.Max(1, _settings.TextReadKiB) * 1024;
        var buffer = new byte[maxBytes];
        int read = stream.Read(buffer, 0, maxBytes);
        if (read <= 0) return "";

        Encoding enc = Encoding.UTF8;
        int skip = 0;
        if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            enc = Encoding.UTF8; skip = 3;
        }
        else if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            enc = Encoding.Unicode; skip = 2;
        }
        else if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            enc = Encoding.BigEndianUnicode; skip = 2;
        }
        else
        {
            enc = LooksLikeUtf8(buffer, read) ? Encoding.UTF8 : Encoding.GetEncoding(932);
        }

        try { return enc.GetString(buffer, skip, read - skip); }
        catch { return Encoding.UTF8.GetString(buffer, 0, read); }
    }

    private static bool LooksLikeUtf8(byte[] buf, int len)
    {
        int i = 0;
        while (i < len)
        {
            byte b = buf[i];
            if (b < 0x80) { i++; continue; }
            int extra;
            if ((b & 0xE0) == 0xC0) extra = 1;
            else if ((b & 0xF0) == 0xE0) extra = 2;
            else if ((b & 0xF8) == 0xF0) extra = 3;
            else return false;
            if (i + extra >= len) return true;
            for (int j = 1; j <= extra; j++)
                if ((buf[i + j] & 0xC0) != 0x80) return false;
            i += extra + 1;
        }
        return true;
    }
}
