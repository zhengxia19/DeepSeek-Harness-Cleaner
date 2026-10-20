using System.Runtime.InteropServices;
using System.Text;
using DeepSeekHarnessCleaner.Infrastructure;

internal static partial class Program
{
    private static int RunDefaultOpenChecks()
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "default-open-" + Guid.NewGuid().ToString("N")));
        var home = Path.Combine(root, ".dsh");
        var attachments = Path.Combine(home, "attachments");
        Directory.CreateDirectory(attachments);
        var textFile = Path.Combine(attachments, "DSHC-acceptance-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(textFile, "Synthetic DeepSeek Harness Cleaner default-application acceptance. No real session data.");
        var image = Path.Combine(attachments, "object-without-extension");
        using (var bitmap = new Bitmap(160, 100))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.LightBlue);
            graphics.DrawString("DSHC synthetic", SystemFonts.DefaultFont, Brushes.Black, 8, 35);
            bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
        }
        var originalHashes = new[] { textFile, image }.ToDictionary(path => path, Hash);
        var matches = new List<nint>();
        try
        {
            var files = new AttachmentInventoryService().Scan(home, []).Items;
            var opener = new AttachmentOpener(copyRoot: Path.Combine(root, "opened-copies"));
            foreach (var item in files)
            {
                var existingWindows = FixtureWindows(string.Empty).ToHashSet();
                var opening = Task.Run(() => opener.Open(home, item)); // Match the UI's background opening path.
                Pump(() => opening.IsCompleted);
                var opened = opening.GetAwaiter().GetResult();
                Check("real default application accepted synthetic attachment", opened.Success && opened.OpenedPath is not null);
                var token = Path.GetFileNameWithoutExtension(opened.OpenedPath!);
                Pump(() => { matches = FixtureWindows(token); return matches.Count > 0; });
                Check("default application window identifies exact synthetic filename", matches.Count > 0);
                Console.WriteLine("DEFAULT_APPLICATION_VERIFIED=" + Path.GetExtension(opened.OpenedPath));
                foreach (var handle in matches.Where(handle => !existingWindows.Contains(handle))) PostMessage(handle, 0x0010, 0, 0); // Never close a preexisting application window.
                matches.Clear();
            }
            Check("actual default opening preserved both source hashes", originalHashes.All(p => Hash(p.Key) == p.Value));
            Console.WriteLine("DEFAULT_OPEN_RESULT=PASS FIXTURE=" + root);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DEFAULT_OPEN_RESULT=FAIL " + ex.GetType().Name + ":" + ex.Message + " FIXTURE=" + root);
            return 1;
        }
    }

    private static List<nint> FixtureWindows(string token)
    {
        var found = new List<nint>();
        EnumWindows((handle, _) =>
        {
            var text = new StringBuilder(1024);
            GetWindowText(handle, text, text.Capacity);
            if (text.ToString().Contains(token, StringComparison.OrdinalIgnoreCase)) found.Add(handle);
            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, uint message, nint wparam, nint lparam);
}
