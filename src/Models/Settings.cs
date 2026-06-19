using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QTBarExtension.Models;

public class TabEntry
{
    public string Path  { get; set; } = "";
    public string Label { get; set; } = "";
}

public class TabGroup
{
    public Guid          Id       { get; set; } = Guid.NewGuid();
    public string        Name     { get; set; } = "グループ";
    public string        Color    { get; set; } = "#5B9BD5";
    public List<TabEntry> Tabs    { get; set; } = [];
    public DateTime      LastUsed { get; set; } = DateTime.Now;
}

public class HistoryItem
{
    public string   Path      { get; set; } = "";
    public DateTime VisitedAt { get; set; } = DateTime.Now;
}

public class AppSettings
{
    public List<TabGroup>    TabGroups        { get; set; } = [];
    public List<HistoryItem> History          { get; set; } = [];
    public int               MaxHistory       { get; set; } = 200;
    // 注意：自動起動の実際の有効/無効はレジストリ（Core.StartupRegistration）が正であり、
    // このフラグはUIから参照/更新されない。settings.jsonの後方互換性のためフィールドのみ残置。
    public bool              StartWithWindows { get; set; } = true;
    public int               BarHeight        { get; set; } = 30;
    public string            Theme            { get; set; } = "system";
    public string            NewTabAction     { get; set; } = "duplicate";
    public bool CtrlClickNewTab      { get; set; } = true;
    public bool MiddleClickCloseTab  { get; set; } = true;
    public bool CloseExplorerOnLast  { get; set; } = false;
    public bool EnableShiftWheelHorizontalScroll { get; set; } = true;
    public bool EnableFullNameTooltip { get; set; } = true;
    public PreviewSettings Preview { get; set; } = new();
    // ── サブフォルダメニュー設定 ──────────────────────────
    public SubFolderMenuSettings SubFolderMenu { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "QTBarExtension");
    private static readonly string _path = Path.Combine(_dir, "settings.json");
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _opts)
                       ?? CreateDefault();
        }
        catch { }
        return CreateDefault();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(s, _opts));
    }

    public static void AddHistory(AppSettings s, string path)
    {
        s.History.RemoveAll(h => h.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        s.History.Insert(0, new HistoryItem { Path = path });
        if (s.History.Count > s.MaxHistory)
            s.History.RemoveRange(s.MaxHistory, s.History.Count - s.MaxHistory);
    }

    private static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.TabGroups.Add(new TabGroup
        {
            Name  = "よく使うフォルダ",
            Color = "#5B9BD5",
            Tabs  =
            [
                new() { Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                new() { Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new() { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
            ]
        });
        return s;
    }
}
