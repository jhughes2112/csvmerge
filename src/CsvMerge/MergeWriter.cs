using System.Text;

namespace CsvMerge;

/// <summary>
/// Serializes a merge result to CSV text. Every column that has at least one
/// conflicted cell gets a companion column, named "&lt;column&gt;&lt;suffix&gt;" and
/// placed immediately after it, holding exactly the displaced competing value —
/// ready to copy over the kept value if that's the right resolution. Row
/// existence conflicts (delete vs. edit) go to one trailing status column named
/// after the bare suffix. The file stays valid CSV throughout; resolving means
/// copying the values you want and deleting the conflict columns.
/// </summary>
public static class MergeWriter
{
    public static string Write(MergeResult result, string delimiter, string newline,
        string conflictSuffix = "_conflict")
    {
        if (result.Lines.Count == 0) return "";
        var header = result.Lines[0];
        int width = header.Cells.Length;

        // Companion column names; a competing column rename names the companion
        // after the displaced name, so both proposals are visible in the header.
        var used = new HashSet<string>(header.Cells);
        var companion = new string?[width];
        for (int c = 0; c < width; c++)
        {
            if (!result.Lines.Any(l => l.CellConflicts?[c] != null)) continue;
            string name = (header.CellConflicts?[c] ?? header.Cells[c]) + conflictSuffix;
            while (!used.Add(name)) name = "_" + name;
            companion[c] = name;
        }

        string? rowStatus = null;
        if (result.Lines.Any(l => l.RowConflict.Length > 0))
        {
            rowStatus = conflictSuffix;
            while (!used.Add(rowStatus)) rowStatus = "_" + rowStatus;
        }

        var sb = new StringBuilder();
        bool isHeader = true;
        foreach (var line in result.Lines)
        {
            for (int c = 0; c < width; c++)
            {
                if (c > 0) sb.Append(delimiter);
                AppendField(sb, line.Cells[c], delimiter);
                if (companion[c] != null)
                {
                    sb.Append(delimiter);
                    AppendField(sb, isHeader ? companion[c]! : line.CellConflicts?[c] ?? "", delimiter);
                }
            }
            if (rowStatus != null)
            {
                sb.Append(delimiter);
                AppendField(sb, isHeader ? rowStatus : line.RowConflict, delimiter);
            }
            sb.Append(newline);
            isHeader = false;
        }
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string value, string delimiter)
    {
        // Leading/trailing whitespace must be quoted or it would be trimmed
        // away (as insignificant unquoted whitespace) on the next parse.
        bool needsQuoting = value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            || value.Contains(delimiter)
            || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        if (!needsQuoting)
        {
            sb.Append(value);
            return;
        }
        sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }
}
