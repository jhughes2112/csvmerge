namespace CsvMerge;

/// <summary>
/// One CSV row of the merged output (the first one emitted is the header).
/// <paramref name="CellConflicts"/> is parallel to <paramref name="Cells"/>:
/// a non-null entry is the displaced competing value for that column (the
/// data cell always keeps the preserved side's value). On the header row a
/// non-null entry is the competing column name. <paramref name="RowConflict"/>
/// flags row-existence conflicts ("deleted in upstream"/"deleted in rebased").
/// </summary>
public sealed record RowLine(string[] Cells, string?[]? CellConflicts = null, string RowConflict = "");

public sealed class MergeResult
{
    public List<RowLine> Lines { get; } = [];
    public int ConflictCount { get; set; }
    public List<string> Warnings { get; } = [];
}
