namespace CsvMerge;

/// <summary>
/// Aligns one side's rows against the base's rows the way git aligns lines:
/// a patience diff over row content (restricted to the columns the two
/// versions share) finds unambiguous anchors, then rows left unmatched inside
/// each gap are paired by cell similarity so an edited row is recognized as an
/// edit rather than a delete plus an unrelated add.
/// </summary>
public sealed class RowAlignment
{
    /// <summary>For each base row index: the side row it maps to, or -1 if deleted on that side. Monotonic.</summary>
    public required int[] BaseToSide { get; init; }

    /// <summary>For each side row index: the base row it maps to, or -1 if inserted on that side.</summary>
    public required int[] SideToBase { get; init; }

    /// <summary>Inserted side rows, as (slot, sideRow) where slot k means "before base row k" (k == base count appends).</summary>
    public required List<(int Slot, int SideRow)> Insertions { get; init; }

    /// <summary>
    /// Moves: a base row this side deleted whose exact content reappears as an
    /// insertion elsewhere was really relocated, not deleted. Maps base row ->
    /// inserted side row and the reverse. Only exact copies (over shared
    /// columns) qualify; a row that was moved AND edited is not tracked.
    /// </summary>
    public required Dictionary<int, int> MoveTargetByBase { get; init; }
    public required Dictionary<int, int> MoveSourceBySideRow { get; init; }
}

public static class RowAligner
{
    public const double SimilarityThreshold = 0.5;
    private const long GapPairingBudget = 250_000; // max nb*ns cells for the DP pairing of one gap

    public static RowAlignment Align(List<string[]> baseRows, List<string[]> sideRows, int[] baseToSideCols)
    {
        int[] sharedBaseCols = Enumerable.Range(0, baseToSideCols.Length)
            .Where(b => baseToSideCols[b] >= 0).ToArray();

        var baseKeys = baseRows.Select(r => Key(r, sharedBaseCols, identity: true, baseToSideCols)).ToArray();
        var sideKeys = sideRows.Select(r => Key(r, sharedBaseCols, identity: false, baseToSideCols)).ToArray();

        var baseToSide = new int[baseRows.Count];
        var sideToBase = new int[sideRows.Count];
        Array.Fill(baseToSide, -1);
        Array.Fill(sideToBase, -1);

        PatienceMatch(baseKeys, sideKeys, baseToSide, sideToBase);
        PairGaps(baseRows, sideRows, baseToSideCols, baseToSide, sideToBase);

        var insertions = new List<(int, int)>();
        int lastBase = -1;
        for (int s = 0; s < sideRows.Count; s++)
        {
            if (sideToBase[s] >= 0) lastBase = sideToBase[s];
            else insertions.Add((lastBase + 1, s));
        }

        // Move detection: an inserted row identical (over shared columns) to a
        // base row this side "deleted" is a relocation. Pair them first-come.
        var moveTargetByBase = new Dictionary<int, int>();
        var moveSourceBySideRow = new Dictionary<int, int>();
        if (sharedBaseCols.Length > 0)
        {
            var unmatchedByKey = new Dictionary<string, Queue<int>>();
            for (int r = 0; r < baseRows.Count; r++)
            {
                if (baseToSide[r] >= 0) continue;
                if (!unmatchedByKey.TryGetValue(baseKeys[r], out var q))
                    unmatchedByKey[baseKeys[r]] = q = new Queue<int>();
                q.Enqueue(r);
            }
            foreach (var (_, s) in insertions)
            {
                if (unmatchedByKey.TryGetValue(sideKeys[s], out var q) && q.Count > 0)
                {
                    int r = q.Dequeue();
                    moveTargetByBase[r] = s;
                    moveSourceBySideRow[s] = r;
                }
            }
        }

        return new RowAlignment
        {
            BaseToSide = baseToSide,
            SideToBase = sideToBase,
            Insertions = insertions,
            MoveTargetByBase = moveTargetByBase,
            MoveSourceBySideRow = moveSourceBySideRow,
        };
    }

    /// <summary>Fraction of shared columns whose cells are equal between a base row and a side row.</summary>
    public static double Similarity(string[] baseRow, string[] sideRow, int[] baseToSideCols)
    {
        int total = 0, equal = 0;
        for (int b = 0; b < baseToSideCols.Length; b++)
        {
            int s = baseToSideCols[b];
            if (s < 0) continue;
            total++;
            if (baseRow[b] == sideRow[s]) equal++;
        }
        return total == 0 ? 0 : (double)equal / total;
    }

    private static string Key(string[] row, int[] sharedBaseCols, bool identity, int[] baseToSideCols)
    {
        var parts = new string[sharedBaseCols.Length];
        for (int i = 0; i < sharedBaseCols.Length; i++)
        {
            int col = identity ? sharedBaseCols[i] : baseToSideCols[sharedBaseCols[i]];
            parts[i] = row[col];
        }
        return string.Join((char)0x1f, parts);
    }

    /// <summary>
    /// Patience diff: match common prefix/suffix, anchor on rows whose content is
    /// unique in both ranges (longest increasing subsequence keeps order), recurse
    /// into the segments between anchors. Iterative to bound stack depth.
    /// </summary>
    private static void PatienceMatch(string[] a, string[] b, int[] ab, int[] ba)
    {
        var work = new Stack<(int A0, int A1, int B0, int B1)>();
        work.Push((0, a.Length, 0, b.Length));

        while (work.Count > 0)
        {
            var (a0, a1, b0, b1) = work.Pop();

            while (a0 < a1 && b0 < b1 && a[a0] == b[b0]) { ab[a0] = b0; ba[b0] = a0; a0++; b0++; }
            while (a1 > a0 && b1 > b0 && a[a1 - 1] == b[b1 - 1]) { a1--; b1--; ab[a1] = b1; ba[b1] = a1; }
            if (a0 >= a1 || b0 >= b1) continue;

            // Values unique within both ranges become anchor candidates.
            var countA = new Dictionary<string, (int Count, int Idx)>();
            for (int i = a0; i < a1; i++)
                countA[a[i]] = countA.TryGetValue(a[i], out var c) ? (c.Count + 1, c.Idx) : (1, i);
            var countB = new Dictionary<string, (int Count, int Idx)>();
            for (int i = b0; i < b1; i++)
                countB[b[i]] = countB.TryGetValue(b[i], out var c) ? (c.Count + 1, c.Idx) : (1, i);

            var anchors = new List<(int Ai, int Bi)>();
            for (int i = a0; i < a1; i++)
            {
                if (countA[a[i]].Count == 1 &&
                    countB.TryGetValue(a[i], out var cb) && cb.Count == 1)
                {
                    anchors.Add((i, cb.Idx));
                }
            }
            if (anchors.Count == 0) continue; // unresolvable gap; similarity pairing handles it

            var chain = LongestIncreasingByB(anchors);

            int prevA = a0, prevB = b0;
            foreach (var (ai, bi) in chain)
            {
                ab[ai] = bi;
                ba[bi] = ai;
                if (ai > prevA && bi > prevB) work.Push((prevA, ai, prevB, bi));
                prevA = ai + 1;
                prevB = bi + 1;
            }
            if (a1 > prevA && b1 > prevB) work.Push((prevA, a1, prevB, b1));
        }
    }

    /// <summary>Longest chain of anchors increasing in both coordinates (input is sorted by Ai).</summary>
    private static List<(int Ai, int Bi)> LongestIncreasingByB(List<(int Ai, int Bi)> anchors)
    {
        var tailIdx = new List<int>();          // index into anchors of the smallest tail Bi per length
        var parent = new int[anchors.Count];

        for (int i = 0; i < anchors.Count; i++)
        {
            int bi = anchors[i].Bi;
            int lo = 0, hi = tailIdx.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (anchors[tailIdx[mid]].Bi < bi) lo = mid + 1; else hi = mid;
            }
            parent[i] = lo > 0 ? tailIdx[lo - 1] : -1;
            if (lo == tailIdx.Count) tailIdx.Add(i); else tailIdx[lo] = i;
        }

        var chain = new List<(int, int)>();
        for (int i = tailIdx.Count > 0 ? tailIdx[^1] : -1; i >= 0; i = parent[i])
            chain.Add(anchors[i]);
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// For every gap between matched rows, pair leftover base rows with leftover
    /// side rows by similarity (order-preserving DP), so edits are recognized.
    /// </summary>
    private static void PairGaps(List<string[]> baseRows, List<string[]> sideRows,
        int[] colMap, int[] baseToSide, int[] sideToBase)
    {
        int prevB = -1, prevS = -1;
        var matches = new List<(int B, int S)>();
        for (int i = 0; i < baseToSide.Length; i++)
            if (baseToSide[i] >= 0) matches.Add((i, baseToSide[i]));
        matches.Add((baseToSide.Length, sideToBase.Length)); // sentinel

        foreach (var (mb, ms) in matches)
        {
            PairGap(baseRows, sideRows, colMap, prevB + 1, mb, prevS + 1, ms, baseToSide, sideToBase);
            prevB = mb;
            prevS = ms;
        }
    }

    private static void PairGap(List<string[]> baseRows, List<string[]> sideRows, int[] colMap,
        int b0, int b1, int s0, int s1, int[] baseToSide, int[] sideToBase)
    {
        int nb = b1 - b0, ns = s1 - s0;
        if (nb <= 0 || ns <= 0) return;

        if (nb == ns)
        {
            // diff3 chunk semantics: the same number of rows replaced between the
            // same anchors is a modification of each row in place, however heavy
            // the edits — no similarity bar.
            for (int i = 0; i < nb; i++)
            {
                baseToSide[b0 + i] = s0 + i;
                sideToBase[s0 + i] = b0 + i;
            }
            return;
        }

        if ((long)nb * ns > GapPairingBudget)
        {
            // Gap too large for the DP: fall back to positional pairing.
            for (int i = 0; i < Math.Min(nb, ns); i++)
            {
                if (Similarity(baseRows[b0 + i], sideRows[s0 + i], colMap) >= SimilarityThreshold)
                {
                    baseToSide[b0 + i] = s0 + i;
                    sideToBase[s0 + i] = b0 + i;
                }
            }
            return;
        }

        // Needleman-Wunsch style alignment maximizing total similarity; a pairing
        // is only allowed when it clears the similarity threshold.
        var score = new double[nb + 1, ns + 1];
        var move = new byte[nb + 1, ns + 1]; // 1 = up (skip base row), 2 = left (skip side row), 3 = diagonal pair
        for (int i = 1; i <= nb; i++) move[i, 0] = 1;
        for (int j = 1; j <= ns; j++) move[0, j] = 2;

        for (int i = 1; i <= nb; i++)
        {
            for (int j = 1; j <= ns; j++)
            {
                double up = score[i - 1, j];
                double left = score[i, j - 1];
                double sim = Similarity(baseRows[b0 + i - 1], sideRows[s0 + j - 1], colMap);
                double diag = sim >= SimilarityThreshold ? score[i - 1, j - 1] + sim : double.NegativeInfinity;

                if (diag >= up && diag >= left) { score[i, j] = diag; move[i, j] = 3; }
                else if (up >= left) { score[i, j] = up; move[i, j] = 1; }
                else { score[i, j] = left; move[i, j] = 2; }
            }
        }

        int bi = nb, sj = ns;
        while (bi > 0 || sj > 0)
        {
            switch (move[bi, sj])
            {
                case 3:
                    baseToSide[b0 + bi - 1] = s0 + sj - 1;
                    sideToBase[s0 + sj - 1] = b0 + bi - 1;
                    bi--; sj--;
                    break;
                case 1: bi--; break;
                default: sj--; break;
            }
        }
    }
}
