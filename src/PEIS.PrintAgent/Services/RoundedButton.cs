using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PEIS.PrintAgent.Services;

/// <summary>
/// 高清抗锯齿圆角按钮组件。
/// 支持自定义圆角半径、悬停/按压状态平滑过渡以及精致边框。
/// </summary>
internal sealed class RoundedButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 6;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color NormalBackColor { get; set; } = Color.FromArgb(241, 245, 249);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBackColor { get; set; } = Color.FromArgb(226, 232, 240);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedBackColor { get; set; } = Color.FromArgb(203, 213, 225);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(203, 213, 225);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BorderSize { get; set; } = 1;

    private bool _isHovered;
    private bool _isPressed;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = NormalBackColor;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        _isPressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isPressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isPressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var rect = ClientRectangle;
        if (rect.Width <= 2 || rect.Height <= 2) return;

        rect.Width -= 1;
        rect.Height -= 1;

        var currentBack = _isPressed ? PressedBackColor : (_isHovered ? HoverBackColor : NormalBackColor);

        using var path = CreateRoundedRectanglePath(rect, CornerRadius);
        using var brush = new SolidBrush(Enabled ? currentBack : Color.FromArgb(241, 245, 249));
        g.FillPath(brush, path);

        if (BorderSize > 0 && BorderColor != Color.Transparent)
        {
            using var pen = new Pen(Enabled ? BorderColor : Color.FromArgb(226, 232, 240), BorderSize);
            g.DrawPath(pen, path);
        }

        var fore = Enabled ? ForeColor : Color.FromArgb(148, 163, 184);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        if (diameter > rect.Width) diameter = rect.Width;
        if (diameter > rect.Height) diameter = rect.Height;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
