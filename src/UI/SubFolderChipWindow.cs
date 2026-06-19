using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QTBarExtension.Core;
using static QTBarExtension.Core.NativeMethods;

namespace QTBarExtension.UI;

/// <summary>
/// フォルダアイコン上に表示する小さな▶チップウィンドウ。
/// icon/sdt_tip.png を使用する（なければ▶テキストでフォールバック）。
/// クリックするとサブフォルダメニューを開くイベントを発行する。
/// </summary>
public class SubFolderChipWindow : Window
{
    public event Action<string, double, double>? ChipClicked;

    private string _folderPath = "";
    private readonly Border _chip;

    // sdt_tip.pngを起動時に一度だけ読み込む
    private static ImageSource? _sdtTipImage;
    private static bool _sdtTipTried;

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

        UIElement iconElement = TryGetSdtTipElement() ?? MakeFallbackIcon();

        _chip = new Border
        {
            Width           = 15,
            Height          = 15,
            CornerRadius    = new CornerRadius(2),
            Background      = new SolidColorBrush(Color.FromArgb(180, 0, 100, 180)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(200, 0, 70, 140)),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Padding         = new Thickness(1),
            Child           = iconElement,
        };

        _chip.MouseEnter += (_, _) =>
            _chip.Background = new SolidColorBrush(Color.FromArgb(230, 0, 120, 212));
        _chip.MouseLeave += (_, _) =>
            _chip.Background = new SolidColorBrush(Color.FromArgb(180, 0, 100, 180));
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

    private static UIElement? TryGetSdtTipElement()
    {
        if (!_sdtTipTried)
        {
            _sdtTipTried = true;
            try
            {
                // 1) まずexe隣のicon/sdt_tip.pngを試みる
                string? exeDir = Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
                if (exeDir != null)
                {
                    string iconPath = Path.Combine(exeDir, "icon", "sdt_tip.png");
                    if (File.Exists(iconPath))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        _sdtTipImage = bmp;
                    }
                }

                // 2) ファイルが見つからない場合、WPFアセンブリ埋め込みリソースから読み込む
                if (_sdtTipImage == null)
                {
                    var uri = new Uri("pack://application:,,,/QTBarExtension;component/icon/sdt_tip.png",
                                      UriKind.Absolute);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = uri;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    _sdtTipImage = bmp;
                }
            }
            catch { }
        }

        if (_sdtTipImage == null) return null;
        return new System.Windows.Controls.Image
        {
            Source              = _sdtTipImage,
            Width               = 13,
            Height              = 13,
            Stretch             = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
    }

    private static UIElement MakeFallbackIcon() => new TextBlock
    {
        Text                = "▶",
        FontSize            = 7,
        Foreground          = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment   = VerticalAlignment.Center,
    };

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
