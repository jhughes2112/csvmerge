namespace CsvMerge;

/// <summary>
/// Git-native three-way CSV merge, tuned for rebase. The base (merge ancestor)
/// is diffed against each side independently — columns first, then rows — and
/// the two change vectors are combined. Independent changes merge cleanly, down
/// to the individual cell. When changes compete, the "ours" side wins the data
/// cells (during a rebase that is the version already committed upstream — the
/// one to preserve) and the displaced "theirs" value is reported in the row's
/// conflict annotation as "column=value", nothing more.
/// </summary>
public static class ThreeWayMerger
{
    /// <summary>A column of the merged output and where its cells come from in each input.</summary>
    private sealed class ResultCol
    {
        public int BaseCol = -1, OursCol = -1, TheirsCol = -1;
        public string Name = "";
        public string? TheirsName; // set only when the two sides renamed it differently
    }

    public static MergeResult Merge(CsvTable baseTable, CsvTable ours, CsvTable theirs,
        string labelOurs = "ours", string labelTheirs = "theirs")
    {
        var result = new MergeResult();

        var oursCols = ColumnAligner.Align(baseTable, ours);
        var theirsCols = ColumnAligner.Align(baseTable, theirs);
        var oursRows = RowAligner.Align(baseTable.Rows, ours.Rows, oursCols.BaseToSide);
        var theirsRows = RowAligner.Align(baseTable.Rows, theirs.Rows, theirsCols.BaseToSide);

        var cols = BuildResultColumns(baseTable, ours, theirs, oursCols, theirsCols, oursRows, theirsRows, result);
        var baseWeights = RowAligner.DistinctivenessWeights(baseTable.Rows, baseTable.Header.Length);
        // Columns new in this merge have unknown distinctiveness; assume full weight.
        var colWeights = cols.Select(c => c.BaseCol >= 0 ? baseWeights[c.BaseCol] : 1.0).ToArray();
        EmitHeader(cols, result);
        EmitRows(cols, colWeights, baseTable, ours, theirs, oursRows, theirsRows, labelOurs, labelTheirs, result);
        return result;
    }

    private static List<ResultCol> BuildResultColumns(CsvTable baseTable, CsvTable ours, CsvTable theirs,
        ColumnMap oursCols, ColumnMap theirsCols, RowAlignment oursRows, RowAlignment theirsRows, MergeResult result)
    {
        var cols = new List<ResultCol>();

        foreach (int b in Enumerable.Range(0, baseTable.Header.Length))
        {
            int oc = oursCols.BaseToSide[b];
            int tc = theirsCols.BaseToSide[b];

            if (oc < 0 && tc < 0) continue; // deleted on both sides

            if (oc < 0 || tc < 0)
            {
                // Deleted on one side. If the surviving side left it completely
                // untouched, the deletion wins; if it renamed it or edited any of
                // its cells, that is a delete/modify conflict — keep the column.
                bool oursSurvives = oc >= 0;
                var side = oursSurvives ? ours : theirs;
                int sc = oursSurvives ? oc : tc;
                var align = oursSurvives ? oursRows : theirsRows;

                bool renamed = side.Header[sc] != baseTable.Header[b];
                bool edited = ColumnEdited(baseTable, side, b, sc, align);
                if (!renamed && !edited) continue;

                result.ConflictCount++;
                result.Warnings.Add(
                    $"column '{baseTable.Header[b]}' was deleted in {(oursSurvives ? "theirs" : "ours")} " +
                    $"but {(renamed ? "renamed" : "modified")} in {(oursSurvives ? "ours" : "theirs")}; keeping it");
                cols.Add(new ResultCol
                {
                    BaseCol = b,
                    OursCol = oc,
                    TheirsCol = tc,
                    Name = side.Header[sc],
                });
                continue;
            }

            // Present on both sides; resolve the name three-way.
            string baseName = baseTable.Header[b], oursName = ours.Header[oc], theirsName = theirs.Header[tc];
            var col = new ResultCol { BaseCol = b, OursCol = oc, TheirsCol = tc };
            if (oursName == theirsName) col.Name = oursName;
            else if (oursName == baseName) col.Name = theirsName;   // theirs renamed it
            else if (theirsName == baseName) col.Name = oursName;   // ours renamed it
            else
            {
                // Both renamed it, differently: ours' name is preserved, theirs'
                // is reported in the header row's conflict annotation.
                col.Name = oursName;
                col.TheirsName = theirsName;
                result.ConflictCount++;
                result.Warnings.Add($"column '{baseName}' renamed to '{oursName}' in ours and '{theirsName}' in theirs; kept '{oursName}'");
            }
            cols.Add(col);
        }

        // Columns added on a side. Same-named additions on both sides become one column.
        var theirsAdded = new List<int>(theirsCols.AddedSideCols);
        foreach (int oc in oursCols.AddedSideCols)
        {
            var col = new ResultCol { OursCol = oc, Name = ours.Header[oc] };
            int match = theirsAdded.FindIndex(tc => theirs.Header[tc] == ours.Header[oc]);
            if (match >= 0)
            {
                col.TheirsCol = theirsAdded[match];
                theirsAdded.RemoveAt(match);
            }
            cols.Add(col);
        }
        foreach (int tc in theirsAdded)
            cols.Add(new ResultCol { TheirsCol = tc, Name = theirs.Header[tc] });

        return OrderColumns(cols);
    }

    /// <summary>True when any matched row's cell in this column differs between base and side.</summary>
    private static bool ColumnEdited(CsvTable baseTable, CsvTable side, int b, int sc, RowAlignment align)
    {
        for (int r = 0; r < baseTable.Rows.Count; r++)
        {
            int sr = align.BaseToSide[r];
            if (sr >= 0 && baseTable.Rows[r][b] != side.Rows[sr][sc]) return true;
        }
        return false;
    }

    /// <summary>
    /// The merged file replaces "ours", so follow ours' column order for the
    /// columns ours has; everything else is inserted after its nearest surviving
    /// left-hand neighbour (theirs order, falling back to base order).
    /// </summary>
    private static List<ResultCol> OrderColumns(List<ResultCol> cols)
    {
        var placed = cols.Where(c => c.OursCol >= 0).OrderBy(c => c.OursCol).ToList();
        var rest = cols.Where(c => c.OursCol < 0).OrderBy(c => c.TheirsCol).ToList();

        foreach (var col in rest)
        {
            int anchor = -1;
            if (col.TheirsCol >= 0)
                for (int p = col.TheirsCol - 1; p >= 0 && anchor < 0; p--)
                    anchor = placed.FindIndex(x => x.TheirsCol == p);
            if (anchor < 0 && col.BaseCol >= 0)
                for (int p = col.BaseCol - 1; p >= 0 && anchor < 0; p--)
                    anchor = placed.FindIndex(x => x.BaseCol == p);
            // Ours-only additions already sitting at this anchor stay ahead of
            // theirs' additions, mirroring the ours-first rule for rows.
            int at = anchor + 1;
            while (at < placed.Count && placed[at].BaseCol < 0 && placed[at].TheirsCol < 0) at++;
            placed.Insert(at, col);
        }
        return placed;
    }

    private static void EmitHeader(List<ResultCol> cols, MergeResult result)
    {
        var names = cols.Select(c => c.Name).ToArray();
        string?[]? conflicts = null;
        for (int i = 0; i < cols.Count; i++)
        {
            if (cols[i].TheirsName == null) continue;
            conflicts ??= new string?[cols.Count];
            conflicts[i] = cols[i].TheirsName;
        }
        result.Lines.Add(new RowLine(names, conflicts));
    }

    private static void EmitRows(List<ResultCol> cols, double[] colWeights, CsvTable baseTable, CsvTable ours, CsvTable theirs,
        RowAlignment oursRows, RowAlignment theirsRows, string labelOurs, string labelTheirs, MergeResult result)
    {
        var oursInsBySlot = oursRows.Insertions.GroupBy(i => i.Slot)
            .ToDictionary(g => g.Key, g => g.Select(i => i.SideRow).ToList());
        var theirsInsBySlot = theirsRows.Insertions.GroupBy(i => i.Slot)
            .ToDictionary(g => g.Key, g => g.Select(i => i.SideRow).ToList());

        for (int slot = 0; slot <= baseTable.Rows.Count; slot++)
        {
            EmitInsertions(cols, colWeights, baseTable, ours, theirs, oursRows, theirsRows,
                oursInsBySlot.GetValueOrDefault(slot), theirsInsBySlot.GetValueOrDefault(slot),
                labelOurs, labelTheirs, result);

            if (slot == baseTable.Rows.Count) break;
            int r = slot;
            int or = oursRows.BaseToSide[r];
            int tr = theirsRows.BaseToSide[r];

            // A row one side relocated is emitted at the mover's new position
            // (with the other side's edits folded in there), not here.
            if ((or < 0 && oursRows.MoveTargetByBase.ContainsKey(r)) ||
                (tr < 0 && theirsRows.MoveTargetByBase.ContainsKey(r)))
                continue;

            if (or < 0 && tr < 0) continue; // deleted on both sides

            if (or < 0 || tr < 0)
            {
                // Deleted on one side; if the other side left it untouched the delete wins,
                // otherwise it is git's classic delete/modify conflict: the edited row is
                // kept and the annotation records who wanted it gone.
                bool oursSurvives = or >= 0;
                var side = oursSurvives ? ours : theirs;
                int sr = oursSurvives ? or : tr;
                bool edited = RowEdited(cols, baseTable.Rows[r], side.Rows[sr], oursSide: oursSurvives);
                if (!edited) continue;

                result.ConflictCount++;
                var survivor = ProjectRow(cols, side.Rows[sr], oursSide: oursSurvives, baseTable.Rows[r]);
                result.Lines.Add(new RowLine(survivor,
                    RowConflict: $"deleted in {(oursSurvives ? labelTheirs : labelOurs)}"));
                continue;
            }

            // Present on both sides: merge cell by cell.
            MergeMatchedRow(cols, baseTable, ours, theirs, r, or, tr, result);
        }
    }

    /// <summary>
    /// Three-way merge of one row present in base, ours and theirs. Competing
    /// cells keep ours' value; theirs' displaced value goes into that column's
    /// conflict companion.
    /// </summary>
    private static void MergeMatchedRow(List<ResultCol> cols, CsvTable baseTable, CsvTable ours, CsvTable theirs,
        int r, int or, int tr, MergeResult result)
    {
        var merged = new string[cols.Count];
        string?[]? conflicts = null;

        for (int i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            string baseVal = col.BaseCol >= 0 ? baseTable.Rows[r][col.BaseCol] : "";
            // A side that lacks the column has no opinion: it inherits the base value
            // for base-derived (resurrected) columns and "" for columns the other side added.
            string oursVal = col.OursCol >= 0 ? ours.Rows[or][col.OursCol] : (col.BaseCol >= 0 ? baseVal : "");
            string theirsVal = col.TheirsCol >= 0 ? theirs.Rows[tr][col.TheirsCol] : (col.BaseCol >= 0 ? baseVal : "");

            if (oursVal == theirsVal) merged[i] = oursVal;
            else if (oursVal == baseVal) merged[i] = theirsVal;
            else if (theirsVal == baseVal) merged[i] = oursVal;
            else
            {
                merged[i] = oursVal;
                (conflicts ??= new string?[cols.Count])[i] = theirsVal;
            }
        }

        if (conflicts != null) result.ConflictCount++;
        result.Lines.Add(new RowLine(merged, conflicts));
    }

    /// <summary>
    /// Rows appearing at this position that are not in the base. An insertion
    /// recognized as a move re-merges the original base row here, folding in the
    /// other side's edits (or vanishing if the other side deleted the row).
    /// Genuine insertions from both sides at the same position: identical
    /// inserts are deduplicated; similar-but-different inserts (both sides
    /// likely added the same logical record) keep ours' cells with theirs'
    /// differing values in the annotation; unrelated inserts are kept — ours first.
    /// </summary>
    private static void EmitInsertions(List<ResultCol> cols, double[] colWeights, CsvTable baseTable, CsvTable ours, CsvTable theirs,
        RowAlignment oursRows, RowAlignment theirsRows,
        List<int>? oursIns, List<int>? theirsIns, string labelOurs, string labelTheirs, MergeResult result)
    {
        var oursProjected = new List<string[]>();
        foreach (int s in oursIns ?? [])
        {
            if (oursRows.MoveSourceBySideRow.TryGetValue(s, out int r))
            {
                // Ours moved base row r here. Find theirs' version of that row:
                // matched in place, moved by theirs too, or deleted.
                int tr = theirsRows.BaseToSide[r];
                if (tr < 0 && theirsRows.MoveTargetByBase.TryGetValue(r, out int theirsTarget)) tr = theirsTarget;
                if (tr < 0)
                {
                    // Theirs deleted the row. An unedited move loses to the
                    // delete; a moved-and-edited row is git's delete/modify
                    // conflict, surfaced at the row's new position.
                    if (!RowEdited(cols, baseTable.Rows[r], ours.Rows[s], oursSide: true)) continue;
                    result.ConflictCount++;
                    result.Lines.Add(new RowLine(
                        ProjectRow(cols, ours.Rows[s], oursSide: true, baseTable.Rows[r]),
                        RowConflict: $"deleted in {labelTheirs}"));
                    continue;
                }
                MergeMatchedRow(cols, baseTable, ours, theirs, r, s, tr, result);
            }
            else
            {
                oursProjected.Add(ProjectRow(cols, ours.Rows[s], oursSide: true, baseRow: null));
            }
        }

        var theirsProjected = new List<string[]>();
        foreach (int s in theirsIns ?? [])
        {
            if (theirsRows.MoveSourceBySideRow.TryGetValue(s, out int r))
            {
                if (oursRows.MoveTargetByBase.ContainsKey(r)) continue; // both moved it; ours' position wins
                int or = oursRows.BaseToSide[r];
                if (or < 0)
                {
                    if (!RowEdited(cols, baseTable.Rows[r], theirs.Rows[s], oursSide: false)) continue;
                    result.ConflictCount++;
                    result.Lines.Add(new RowLine(
                        ProjectRow(cols, theirs.Rows[s], oursSide: false, baseTable.Rows[r]),
                        RowConflict: $"deleted in {labelOurs}"));
                    continue;
                }
                MergeMatchedRow(cols, baseTable, ours, theirs, r, or, s, result);
            }
            else
            {
                theirsProjected.Add(ProjectRow(cols, theirs.Rows[s], oursSide: false, baseRow: null));
            }
        }

        var theirsUsed = new bool[theirsProjected.Count];

        foreach (var oursRow in oursProjected)
        {
            int identical = -1, similar = -1;
            double bestSim = 0;
            for (int j = 0; j < theirsProjected.Count; j++)
            {
                if (theirsUsed[j]) continue;
                if (oursRow.SequenceEqual(theirsProjected[j])) { identical = j; break; }
                double sim = CellSimilarity(oursRow, theirsProjected[j], colWeights);
                if (sim >= RowAligner.SimilarityThreshold && sim > bestSim) { bestSim = sim; similar = j; }
            }

            if (identical >= 0)
            {
                theirsUsed[identical] = true;
                result.Lines.Add(new RowLine(oursRow)); // both sides added the same row
            }
            else if (similar >= 0)
            {
                theirsUsed[similar] = true;
                result.ConflictCount++;
                var theirsRow = theirsProjected[similar];
                var conflicts = new string?[cols.Count];
                for (int i = 0; i < cols.Count; i++)
                    if (oursRow[i] != theirsRow[i])
                        conflicts[i] = theirsRow[i];
                result.Lines.Add(new RowLine(oursRow, conflicts));
            }
            else
            {
                result.Lines.Add(new RowLine(oursRow));
            }
        }

        for (int j = 0; j < theirsProjected.Count; j++)
            if (!theirsUsed[j])
                result.Lines.Add(new RowLine(theirsProjected[j]));
    }

    private static double CellSimilarity(string[] a, string[] b, double[] weights)
    {
        double total = 0, equal = 0;
        for (int i = 0; i < a.Length; i++)
        {
            total += weights[i];
            if (a[i] == b[i]) equal += weights[i];
        }
        return total <= 0 ? 0 : equal / total;
    }

    /// <summary>True when the side changed the base row in any merged column (including values in columns it added).</summary>
    private static bool RowEdited(List<ResultCol> cols, string[] baseRow, string[] sideRow, bool oursSide)
    {
        foreach (var col in cols)
        {
            int sc = oursSide ? col.OursCol : col.TheirsCol;
            if (sc < 0) continue;
            string baseVal = col.BaseCol >= 0 ? baseRow[col.BaseCol] : "";
            if (sideRow[sc] != baseVal) return true;
        }
        return false;
    }

    /// <summary>Express one side's row in the merged column layout.</summary>
    private static string[] ProjectRow(List<ResultCol> cols, string[] sideRow, bool oursSide, string[]? baseRow)
    {
        var cells = new string[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            int sc = oursSide ? col.OursCol : col.TheirsCol;
            if (sc >= 0) cells[i] = sideRow[sc];
            else if (col.BaseCol >= 0 && baseRow != null) cells[i] = baseRow[col.BaseCol];
            else cells[i] = "";
        }
        return cells;
    }
}
