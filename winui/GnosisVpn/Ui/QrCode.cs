using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace GnosisVpn.Ui;

/// <summary>A QR code of a text (the node's address), drawn from QRCoder's module grid as
/// rectangles so it stays sharp at every display scaling.</summary>
public static class QrCode
{
    public static FrameworkElement For(string text, double size)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;   // includes the quiet zone
        int n = m.Count;
        var cv = new Canvas { Width = n, Height = n, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
        var dark = new SolidColorBrush(Microsoft.UI.Colors.Black);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y][x])
                {
                    var r = new Rectangle { Width = 1.02, Height = 1.02, Fill = dark };
                    Canvas.SetLeft(r, x); Canvas.SetTop(r, y); cv.Children.Add(r);
                }
        var vb = new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = cv };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(vb, "QR code of " + text);
        return vb;
    }
}
