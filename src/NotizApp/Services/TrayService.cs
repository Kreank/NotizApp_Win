using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace NotizApp.Services;

/// <summary>
/// Tray-Icon über WinForms NotifyIcon. Das Icon wird zur Laufzeit gezeichnet
/// (blauer Kreis mit "N"), damit keine Icon-Ressource nötig ist.
/// </summary>
public sealed class TrayService : IDisposable
{
    WF.NotifyIcon? _icon;

    public event Action? OeffnenGeklickt;
    public event Action? SchnellnotizGeklickt;
    public event Action? BeendenGeklickt;

    public void Anzeigen()
    {
        if (_icon is not null) return;

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => OeffnenGeklickt?.Invoke());
        menu.Items.Add("Schnellnotiz  (Strg+Alt+N)", null, (_, _) => SchnellnotizGeklickt?.Invoke());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => BeendenGeklickt?.Invoke());

        _icon = new WF.NotifyIcon
        {
            Icon = ZeichneIcon(),
            Text = "NotizApp",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OeffnenGeklickt?.Invoke();
    }

    static SD.Icon ZeichneIcon()
    {
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            using var brush = new SD.SolidBrush(SD.Color.FromArgb(0, 103, 192)); // Win11-Akzentblau
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new SD.Font("Segoe UI", 15, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
            var format = new SD.StringFormat
            {
                Alignment = SD.StringAlignment.Center,
                LineAlignment = SD.StringAlignment.Center,
            };
            g.DrawString("N", font, SD.Brushes.White, new SD.RectangleF(0, 0, 32, 32), format);
        }
        var handle = bmp.GetHicon();
        return SD.Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
