namespace CsvMerge;

/// <summary>
/// Maps the base table's columns onto one side's columns, detecting renames,
/// deletions, additions and reorders. Position is never trusted on its own:
/// exact header names win first, then column content similarity, and only
/// then position as a tie-break.
/// </summary>
public sealed class ColumnMap
{
    /// <summary>For each base column index: the side column index it maps to, or -1 if deleted on that side.</summary>
    public required int[] BaseToSide { get; init; }

    /// <summary>Side column indices that map to no base column (added on that side), in side order.</summary>
    public required List<int> AddedSideCols { get; init; }
}

public static class ColumnAligner
{
    private const double RenameThreshold = 0.5;      // content overlap that confirms a rename outright
    private const double PositionalThreshold = 0.2;  // weaker overlap accepted when the column kept its position
    private const int SampleRows = 500;              // rows sampled for content comparison

    public static ColumnMap Align(CsvTable baseTable, CsvTable side)
    {
        int nb = baseTable.Header.Length, ns = side.Header.Length;
        var baseToSide = new int[nb];
        Array.Fill(baseToSide, -1);
        var sideUsed = new bool[ns];

        // Pass 1: exact header-name matches, paired by order of occurrence so
        // duplicate column names still map sanely.
        var byName = new Dictionary<string, Queue<int>>();
        for (int s = 0; s < ns; s++)
        {
            if (!byName.TryGetValue(side.Header[s], out var q))
                byName[side.Header[s]] = q = new Queue<int>();
            q.Enqueue(s);
        }
        for (int b = 0; b < nb; b++)
        {
            if (byName.TryGetValue(baseTable.Header[b], out var q) && q.Count > 0)
            {
                int s = q.Dequeue();
                baseToSide[b] = s;
                sideUsed[s] = true;
            }
        }

        // Pass 2: rename detection among the leftovers by content overlap.
        var leftoverBase = Enumerable.Range(0, nb).Where(b => baseToSide[b] < 0).ToList();
        var leftoverSide = Enumerable.Range(0, ns).Where(s => !sideUsed[s]).ToList();

        if (leftoverBase.Count > 0 && leftoverSide.Count > 0)
        {
            var candidates = new List<(double Score, int B, int S)>();
            foreach (int b in leftoverBase)
                foreach (int s in leftoverSide)
                    candidates.Add((ContentOverlap(baseTable.Rows, b, side.Rows, s), b, s));

            foreach (var (score, b, s) in candidates.OrderByDescending(c => c.Score))
            {
                if (score < RenameThreshold) break;
                if (baseToSide[b] >= 0 || sideUsed[s]) continue;
                baseToSide[b] = s;
                sideUsed[s] = true;
            }

            // Pass 3: positional fallback — a column that stayed in the same slot
            // and has at least weak content overlap (or no data to compare) is
            // treated as a rename rather than a delete + add.
            foreach (int b in leftoverBase)
            {
                if (baseToSide[b] >= 0) continue;
                if (b >= ns || sideUsed[b]) continue;
                double score = ContentOverlap(baseTable.Rows, b, side.Rows, b);
                if (score < 0 || score >= PositionalThreshold)
                {
                    baseToSide[b] = b;
                    sideUsed[b] = true;
                }
            }
        }

        var added = Enumerable.Range(0, ns).Where(s => !sideUsed[s]).ToList();
        return new ColumnMap { BaseToSide = baseToSide, AddedSideCols = added };
    }

    /// <summary>
    /// Multiset overlap (Jaccard) of the two columns' sampled values.
    /// Returns -1 when either table has no rows to compare.
    /// </summary>
    private static double ContentOverlap(List<string[]> baseRows, int b, List<string[]> sideRows, int s)
    {
        if (baseRows.Count == 0 || sideRows.Count == 0) return -1;

        var counts = new Dictionary<string, (int A, int B)>();
        for (int i = 0; i < Math.Min(baseRows.Count, SampleRows); i++)
        {
            var v = baseRows[i][b];
            counts[v] = counts.TryGetValue(v, out var c) ? (c.A + 1, c.B) : (1, 0);
        }
        for (int i = 0; i < Math.Min(sideRows.Count, SampleRows); i++)
        {
            var v = sideRows[i][s];
            counts[v] = counts.TryGetValue(v, out var c) ? (c.A, c.B + 1) : (0, 1);
        }

        long intersection = 0, union = 0;
        foreach (var (a, bb) in counts.Values)
        {
            intersection += Math.Min(a, bb);
            union += Math.Max(a, bb);
        }
        return union == 0 ? -1 : (double)intersection / union;
    }
}
