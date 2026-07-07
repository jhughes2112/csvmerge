using System.Text;

namespace CsvMerge;

/// <summary>
/// Cell-level semantic diff built on the same alignment engine as the merge:
/// columns match by name and content (renames recognized), rows align by
/// content with move tracking, and only real content changes are reported —
/// quote-only and whitespace-only differences are invisible. Rows are labelled
/// by the most distinctive surviving column (an auto-detected key) when one
/// exists, otherwise by row number.
///
/// Output lines:
///   ~ col qty -> count                          column renamed
///   + col stock / - col origin                  column added / removed
///   > cols reordered: id,name,qty -> qty,id,name
///   ~ row [id=2]: qty: 20 -> 25; color: a -> b  cells changed
///   - row [id=4]: 4,durian,5                    row removed (with content)
///   + row [id=7]: 7,grape,60                    row added (with content)
///   > row [id=3] moved (3 -> 1)                 row relocated
///   > row [id=5] moved (5 -> 2): qty: 50 -> 51  relocated and edited
/// </summary>
public static class SemanticDiff
{
    public static (string Text, bool Different) Diff(CsvTable oldTable, CsvTable newTable, string delimiter)
    {
        var cols = ColumnAligner.Align(oldTable, newTable);
        var rows = RowAligner.Align(oldTable.Rows, newTable.Rows, cols.BaseToSide);
        var weights = RowAligner.DistinctivenessWeights(oldTable.Rows, oldTable.Header.Length);

        // Auto-detect the discriminating column for row labels: the most
        // distinctive old column that survives into the new file.
        int labelCol = -1;
        double bestWeight = 0.9;
        for (int b = 0; b < weights.Length; b++)
        {
            if (cols.BaseToSide[b] >= 0 && weights[b] > bestWeight)
            {
                bestWeight = weights[b];
                labelCol = b;
            }
        }

        var sb = new StringBuilder();

        // Column changes.
        for (int b = 0; b < oldTable.Header.Length; b++)
        {
            int s = cols.BaseToSide[b];
            if (s < 0)
                sb.Append("- col ").Append(oldTable.Header[b]).Append('\n');
            else if (oldTable.Header[b] != newTable.Header[s])
                sb.Append("~ col ").Append(oldTable.Header[b]).Append(" -> ").Append(newTable.Header[s]).Append('\n');
        }
        foreach (int s in cols.AddedSideCols)
            sb.Append("+ col ").Append(newTable.Header[s]).Append('\n');

        var mapped = new List<int>();
        for (int b = 0; b < oldTable.Header.Length; b++)
            if (cols.BaseToSide[b] >= 0) mapped.Add(cols.BaseToSide[b]);
        bool reordered = false;
        for (int i = 1; i < mapped.Count; i++)
            if (mapped[i] < mapped[i - 1]) reordered = true;
        if (reordered)
        {
            string before = string.Join(",", mapped.Select(s => newTable.Header[s]));
            string after = string.Join(",", mapped.OrderBy(s => s).Select(s => newTable.Header[s]));
            sb.Append("> cols reordered: ").Append(before).Append(" -> ").Append(after).Append('\n');
        }

        // Row changes, deletions and moves, in old-file order.
        for (int r = 0; r < oldTable.Rows.Count; r++)
        {
            int sr = rows.BaseToSide[r];
            if (sr >= 0)
            {
                string changes = CellChanges(oldTable, newTable, cols, r, sr, delimiter);
                if (changes.Length > 0)
                    sb.Append("~ row ").Append(OldLabel(r)).Append(": ").Append(changes).Append('\n');
            }
            else if (rows.MoveTargetByBase.TryGetValue(r, out int target))
            {
                string changes = CellChanges(oldTable, newTable, cols, r, target, delimiter);
                sb.Append("> row ").Append(OldLabel(r))
                    .Append(" moved (").Append(r + 1).Append(" -> ").Append(target + 1).Append(')');
                if (changes.Length > 0) sb.Append(": ").Append(changes);
                sb.Append('\n');
            }
            else
            {
                sb.Append("- row ").Append(OldLabel(r)).Append(": ")
                    .Append(MergeWriter.FormatRow(oldTable.Rows[r], delimiter)).Append('\n');
            }
        }

        // Genuine additions, in new-file order.
        foreach (var (_, s) in rows.Insertions)
        {
            if (rows.MoveSourceBySideRow.ContainsKey(s)) continue;
            sb.Append("+ row ").Append(NewLabel(s)).Append(": ")
                .Append(MergeWriter.FormatRow(newTable.Rows[s], delimiter)).Append('\n');
        }

        return (sb.ToString(), sb.Length > 0);

        string OldLabel(int r) => labelCol < 0
            ? $"[#{r + 1}]"
            : $"[{newTable.Header[cols.BaseToSide[labelCol]]}={oldTable.Rows[r][labelCol]}]";

        string NewLabel(int s) => labelCol < 0
            ? $"[#{s + 1}]"
            : $"[{newTable.Header[cols.BaseToSide[labelCol]]}={newTable.Rows[s][cols.BaseToSide[labelCol]]}]";
    }

    private static string CellChanges(CsvTable oldTable, CsvTable newTable, ColumnMap cols,
        int r, int sr, string delimiter)
    {
        var parts = new List<string>();
        for (int b = 0; b < oldTable.Header.Length; b++)
        {
            int s = cols.BaseToSide[b];
            if (s < 0) continue;
            string oldVal = oldTable.Rows[r][b];
            string newVal = newTable.Rows[sr][s];
            if (oldVal != newVal)
                parts.Add($"{newTable.Header[s]}: {MergeWriter.FormatField(oldVal, delimiter)} -> {MergeWriter.FormatField(newVal, delimiter)}");
        }
        return string.Join("; ", parts);
    }
}
