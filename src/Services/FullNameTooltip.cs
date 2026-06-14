using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.Services;

/// <summary>
/// 詳細表示で省略されたファイル名をホバー表示するための独自ツールチップウィンドウ。
/// クリックを奪わないように WS_EX_TRANSPARENT | WS_EX_NOACTIVATE を設定する。
/// </summary>
public class FullNameTooltip : Window
{
    private readonly TextBlock _text;

    public FullNameTooltip()
    {
        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;
        Topmost            = true;
        ResizeMode         = ResizeMode.NoResize;
        IsHitTestVisible   = false;
        SizeToContent      = SizeToContent.WidthAndHeight;
        Left = -9999; Top = -9999;

        var border = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(255, 255, 225)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2, 4, 2),
        };
        _text = new TextBlock
        {
            FontSize   = 12,
            Foreground = Brushes.Black,
        };
        border.Child = _text;
        Content = border;

        SourceInitialized += (_, _) =>
        {
            var hwnd    = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    public void SetText(string text) => _text.Text = text;

    public void MoveTo(double screenX, double screenY, double rowHeight)
    {
        Left = screenX;
        Top  = screenY;
    }
}
