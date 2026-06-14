using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QTBarExtension.Core;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

/// <summary>
/// フォルダアイコン上に表示する小さな▶チップウィンドウ。
/// クリックするとサブフォルダメニューを開くイベントを発行する。
/// </summary>
public class SubFolderChipWindow : Window
{
    public event Action<string, double, double>? ChipClicked;

    private string _folderPath = "";
    private readonly Border _chip;

    public SubFolderChipWindow()
    {
        WindowStyle        = WindowStyle.None;
        AllowsTransparency = true;
        Background         = Brushes.Transparent;
        ShowInTaskbar      = false;
        Topmost            = true;
        ResizeMode         = ResizeMode.NoResize;
        SizeToContent      = SizeToContent.WidthAndHeight;
        Left = -9999; Top = -9999;
        IsHitTestVisible   = true;

        _chip = new Border
        {
            Width           = 14,
            Height          = 14,
            CornerRadius    = new CornerRadius(2),
            Background      = new SolidColorBrush(Color.FromArgb(200, 0, 120, 212)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(255, 0, 90, 160)),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Child = new TextBlock
            {
                Text                = "▶",
                FontSize            = 7,
                Foreground          = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };

        _chip.MouseEnter += (_, _) =>
            _chip.Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        _chip.MouseLeave += (_, _) =>
            _chip.Background = new SolidColorBrush(Color.FromArgb(200, 0, 120, 212));
        _chip.MouseLeftButtonDown += (_, e) =>
        {
            var pt = _chip.PointToScreen(new Point(_chip.ActualWidth, 0));
            ChipClicked?.Invoke(_folderPath, pt.X, pt.Y);
            e.Handled = true;
        };

        Content = _chip;

        SourceInitialized += (_, _) =>
        {
            var hwnd    = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void ShowAt(double screenX, double screenY, string folderPath)
    {
        _folderPath = folderPath;
        Left = screenX;
        Top  = screenY;
        if (!IsVisible) Show();
    }

    public void HideChip()
    {
        if (IsVisible) Hide();
    }
}
