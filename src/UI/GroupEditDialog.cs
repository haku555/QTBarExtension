using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QTBarExtension.Models;

namespace QTBarExtension.UI;

public class GroupEditDialog : Window
{
    public TabGroup? Result { get; private set; }

    private readonly TextBox    _nameBox;
    private readonly ListBox    _colorList;
    private readonly Border     _previewSwatch;

    public static readonly (string Label, string Hex)[] Colors =
    [
        ("🟦 青",   "#5B9BD5"),
        ("🟩 緑",   "#70AD47"),
        ("🟥 赤",   "#FF5050"),
        ("🟧 橙",   "#ED7D31"),
        ("🟪 紫",   "#9966CC"),
        ("⬜ 灰",   "#808080"),
        ("🟫 茶",   "#8B4513"),
        ("🩵 水色", "#00B0C8"),
        ("🩷 桃",   "#FF69B4"),
        ("🟨 黄",   "#FFD700"),
    ];

    public GroupEditDialog(TabGroup? existing)
    {
        Title  = existing == null ? "グループを追加" : "グループを編集";
        Width  = 340;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(12) };
        for (int i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition()); // 色リスト（伸縮）
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // ボタン

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        void SetPos(UIElement el, int row, int col, int colSpan = 1)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col);
            if (colSpan > 1) Grid.SetColumnSpan(el, colSpan);
            grid.Children.Add(el);
        }

        // グループ名
        var nameLbl = new TextBlock { Text = "グループ名:", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _nameBox    = new TextBox { Text = existing?.Name ?? "新しいグループ", Margin = new Thickness(0, 0, 0, 8) };
        SetPos(nameLbl, 0, 0);
        SetPos(_nameBox, 0, 1);

        // 色プレビュー帯
        var colorLbl = new TextBlock { Text = "色:", Margin = new Thickness(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        _previewSwatch = new Border
        {
            Height = 20, CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 4),
        };
        SetPos(colorLbl,    1, 0);
        SetPos(_previewSwatch, 1, 1);

        // 色リスト
        var colorHdr = new TextBlock { Text = "色を選択:", Margin = new Thickness(0, 0, 0, 2), FontSize = 10, Foreground = Brushes.Gray };
        SetPos(colorHdr, 2, 0, 2);

        _colorList = new ListBox
        {
            Margin    = new Thickness(0, 0, 0, 8),
            MaxHeight = 100,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_colorList, ScrollBarVisibility.Disabled);
        // カラーアイテムを横並びで表示
        var wrap = new WrapPanel();
        foreach (var (label, hex) in Colors)
        {
            var clr = HexToColor(hex);
            var item = new ListBoxItem
            {
                Tag     = hex,
                Padding = new Thickness(4, 2, 4, 2),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Border
                        {
                            Width = 16, Height = 16,
                            Background = new SolidColorBrush(clr),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(180,180,180)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(2),
                            Margin = new Thickness(0, 0, 4, 0),
                        },
                        new TextBlock { Text = label.Split(' ')[1], FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    }
                }
            };
            _colorList.Items.Add(item);
        }
        _colorList.SelectionChanged += (_, _) => UpdatePreview();
        SetPos(_colorList, 3, 0, 2);

        // 初期選択
        int selectedIdx = 0;
        if (existing != null)
            for (int i = 0; i < Colors.Length; i++)
                if (Colors[i].Hex.Equals(existing.Color, StringComparison.OrdinalIgnoreCase))
                { selectedIdx = i; break; }
        _colorList.SelectedIndex = selectedIdx;
        UpdatePreview();

        // ボタン
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok     = new Button { Content = "OK",         Width = 70, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 80, IsCancel = true };
        ok.Click     += (_, _) => { Build(); DialogResult = true; };
        cancel.Click += (_, _) => { DialogResult = false; };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        SetPos(btns, 4, 0, 2);

        Content = grid;
    }

    private void UpdatePreview()
    {
        if (_colorList.SelectedItem is ListBoxItem item && item.Tag is string hex)
            _previewSwatch.Background = new SolidColorBrush(HexToColor(hex));
    }

    private void Build()
    {
        string hex = "#5B9BD5";
        if (_colorList.SelectedItem is ListBoxItem item && item.Tag is string h)
            hex = h;

        Result = new TabGroup
        {
            Name  = _nameBox.Text.Trim() is { Length: > 0 } n ? n : "グループ",
            Color = hex,
        };
    }

    public static Color HexToColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return Color.FromRgb(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        catch { return System.Windows.Media.Colors.SteelBlue; }
    }
}
