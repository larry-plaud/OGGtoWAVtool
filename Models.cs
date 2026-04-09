using System.IO;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using System.Windows.Media;

namespace OggConverter
{
    // ── Folder entry shown in the list ────────────────────────────────────────
    public class FolderEntry
    {
        public string FullPath    { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string OggCount    { get; set; } = "0 OGG";

        public static FolderEntry From(string path)
        {
            int count = Directory.GetFiles(path, "*.ogg", SearchOption.AllDirectories).Length;
            return new FolderEntry
            {
                FullPath    = path,
                DisplayName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                OggCount    = $"{count} OGG"
            };
        }
    }

    // ── Log severity ──────────────────────────────────────────────────────────
    public enum LogLevel { Info, Success, Warning, Error }

    // ── One log line with colour ──────────────────────────────────────────────
    public class LogEntry
    {
        public string   Message { get; set; } = "";
        public LogLevel Level   { get; set; } = LogLevel.Info;

        public Brush Color => Level switch
        {
            LogLevel.Success => new SolidColorBrush(WpfColor.FromRgb(74,  222, 128)),
            LogLevel.Warning => new SolidColorBrush(WpfColor.FromRgb(250, 204, 21)),
            LogLevel.Error   => new SolidColorBrush(WpfColor.FromRgb(248, 113, 113)),
            _                => new SolidColorBrush(WpfColor.FromRgb(107, 104, 130)),
        };

        public string Prefix => Level switch
        {
            LogLevel.Success => "✓ ",
            LogLevel.Warning => "⚠ ",
            LogLevel.Error   => "✗ ",
            _                => "· ",
        };
    }
}
