namespace DeepSeekHarnessCleaner.UI;

internal static class OwnedFormPosition
{
    // CenterParent alone does not position an owned modeless window.
    public static void Center(Form form)
    {
        if (form.Owner is not { } owner) return;
        var work = Screen.FromRectangle(owner.Bounds).WorkingArea;
        var x = owner.Left + (owner.Width - form.Width) / 2;
        var y = owner.Top + (owner.Height - form.Height) / 2;
        form.Location = new Point(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - form.Width)),
            Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - form.Height)));
    }
}
