namespace DeepSeekHarnessCleaner.UI;

/// <summary>Header-only styling; item fonts and native selection drawing remain unchanged.</summary>
internal static class TableHeaders
{
    public static void Apply(DataGridView grid)
    {
        Font? bold = null;
        void RefreshStyle()
        {
            var previous = bold;
            bold = new Font(grid.Font, FontStyle.Bold);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.Font = bold;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            previous?.Dispose();
        }
        RefreshStyle();
        grid.FontChanged += (_, _) => RefreshStyle();
        grid.DpiChangedAfterParent += (_, _) => RefreshStyle();
        grid.Disposed += (_, _) => bold?.Dispose();
    }

    public static void Apply(ListView list)
    {
        var bold = new Font(list.Font, FontStyle.Bold);
        void RefreshFont()
        {
            var previous = bold;
            bold = new Font(list.Font, FontStyle.Bold);
            previous.Dispose();
            list.Invalidate();
        }
        list.OwnerDraw = true;
        list.DrawColumnHeader += (_, e) =>
        {
            e.DrawBackground();
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, bold, Rectangle.Inflate(e.Bounds, -4, 0),
                SystemColors.ControlText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.FontChanged += (_, _) => RefreshFont();
        list.DpiChangedAfterParent += (_, _) => RefreshFont();
        list.Disposed += (_, _) => bold.Dispose();
    }
}
