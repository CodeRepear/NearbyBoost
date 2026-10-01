using System.Collections.Generic;
using System.IO;

namespace NearbyBoostSenderGui;

public record FileBadge(string Label, string ColorHex);

/// <summary>A small colored label standing in for a file-type icon (e.g. "IMG" on a blue chip).</summary>
public static class FileTypeBadge
{
    private static readonly HashSet<string> ImageExt = new() { "jpg", "jpeg", "png", "gif", "webp", "bmp", "heic", "svg" };
    private static readonly HashSet<string> VideoExt = new() { "mp4", "mkv", "mov", "avi", "webm", "m4v", "3gp" };
    private static readonly HashSet<string> AudioExt = new() { "mp3", "wav", "aac", "flac", "ogg", "m4a" };
    private static readonly HashSet<string> DocExt = new() { "doc", "docx", "txt", "rtf", "odt" };
    private static readonly HashSet<string> PdfExt = new() { "pdf" };
    private static readonly HashSet<string> SheetExt = new() { "xls", "xlsx", "csv" };
    private static readonly HashSet<string> SlideExt = new() { "ppt", "pptx" };
    private static readonly HashSet<string> ArchiveExt = new() { "zip", "rar", "7z", "tar", "gz" };
    private static readonly HashSet<string> CodeExt = new() { "cs", "kt", "java", "py", "js", "ts", "json", "xml", "html", "css" };

    public static FileBadge ForFile(string name)
    {
        string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ImageExt.Contains(ext)) return new FileBadge("IMG", "#2E86AB");
        if (VideoExt.Contains(ext)) return new FileBadge("VID", "#EF476F");
        if (AudioExt.Contains(ext)) return new FileBadge("AUD", "#06D6A0");
        if (PdfExt.Contains(ext)) return new FileBadge("PDF", "#E63946");
        if (DocExt.Contains(ext)) return new FileBadge("DOC", "#118AB2");
        if (SheetExt.Contains(ext)) return new FileBadge("XLS", "#2A9D8F");
        if (SlideExt.Contains(ext)) return new FileBadge("PPT", "#F4A261");
        if (ArchiveExt.Contains(ext)) return new FileBadge("ZIP", "#6A4C93");
        if (CodeExt.Contains(ext)) return new FileBadge("DEV", "#264653");
        return new FileBadge("FILE", "#6C757D");
    }

    public static FileBadge ForFolder() => new("DIR", "#FFB703");
}
