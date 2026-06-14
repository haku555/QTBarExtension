using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

/// <summary>
/// タブドラッグ中にカーソルに追従する半透明ゴーストウィンドウ。
/// 元のタブと同じサイズ・ダークモード対応。
/// </summary>
public class DragGhostWindow : Window
{
    public DragGhostWindow(string label, Color accentColor,
        double tabWidth, double tabHeight, bool isDark)
    {
        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;
        Topmost            = true;
        ResizeMode         = ResizeMode.NoResize;
        IsHitTestVisible   = false;
        Width              = tabWidth;
        Height             = tabHeight;
        Left               = -9999;
        Top                = -9999;

        // ダークモード対応カラー
        Color bgColor   = isDark
            ? Color.FromArgb(140, 55,  55,  55)
            : Color.FromArgb(140, 255, 255, 255);
        Color textColor = isDark
            ? Color.FromRgb(230, 230, 230)
            : Color.FromRgb(10,  10,  10);

        var root = new Border
        {
            Background      = new SolidColorBrush(bgColor),
            BorderBrush     = new SolidColorBrush(accentColor),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4, 4, 0, 0),
            Opacity         = 0.45,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        grid.RowDefinitions.Add(new RowDefinition());

        // アクセントライン
        var accentLine = new Border
        {
            Background   = new SolidColorBrush(accentColor),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
        };
        Grid.SetRow(accentLine, 0);

        // コンテンツ
        var content = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0, 4, 0),
        };
        content.Children.Add(new TextBlock
        {
            Text = "📁 ", FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text         = label,
            FontSize     = 11,
            FontWeight   = FontWeights.SemiBold,
            Foreground   = new SolidColorBrush(textColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth     = tabWidth - 36,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetRow(content, 1);

        grid.Children.Add(accentLine);
        grid.Children.Add(content);
        root.Child = grid;
        Content    = root;

        // フォーカスを奪わない
        SourceInitialized += (_, _) =>
        {
            var hwnd    = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    /// <summary>スクリーン座標でゴーストを配置</summary>
    public void MoveTo(double screenX, double screenY)
    {
        Left = screenX;
        Top  = screenY;
    }
}
