using System.Windows.Media.Imaging;

namespace QHR.Models;

public sealed class DailyEvidence
{
    public int Version { get; set; } = 1;
    public DateOnly Date { get; set; }
    public string Note { get; set; } = string.Empty;
    public List<EvidenceAttachment> Images { get; set; } = [];
}

public sealed class EvidenceAttachment
{
    public string Id { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class EvidenceImageItem
{
    public required EvidenceAttachment Attachment { get; init; }
    public required BitmapSource Preview { get; init; }
    public string FileName => Attachment.OriginalFileName;
    public string SizeText => Attachment.Length >= 1024 * 1024
        ? $"{Attachment.Length / 1024d / 1024d:N1} MB"
        : $"{Math.Max(1, Attachment.Length / 1024d):N0} KB";
}
