namespace QHR.Models;

public enum BackupConflictMode
{
    KeepLocal,
    UseBackup
}

public sealed class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string AppVersion { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public int AttendanceCount { get; set; }
    public int ExpenseCount { get; set; }
    public int EvidenceDayCount { get; set; }
    public int EvidenceImageCount { get; set; }
}

public sealed class BackupConflictSummary
{
    public bool SettingsConflict { get; init; }
    public bool GoalSettingsConflict { get; init; }
    public int AttendanceConflicts { get; init; }
    public int ExpenseConflicts { get; init; }
    public int CompletedGoalConflicts { get; init; }
    public int EvidenceNoteConflicts { get; init; }
    public int EvidenceImageConflicts { get; init; }

    public int TotalConflicts =>
        (SettingsConflict ? 1 : 0) +
        (GoalSettingsConflict ? 1 : 0) +
        AttendanceConflicts +
        ExpenseConflicts +
        CompletedGoalConflicts +
        EvidenceNoteConflicts +
        EvidenceImageConflicts;
}

public sealed class BackupExportResult
{
    public required BackupManifest Manifest { get; init; }
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
}

public sealed class BackupImportResult
{
    public int AttendanceCount { get; init; }
    public int ExpenseCount { get; init; }
    public int EvidenceDayCount { get; init; }
    public int EvidenceImageCount { get; init; }
}
