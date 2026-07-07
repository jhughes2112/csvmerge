# csvmerge

A git-native three-way merge driver for CSV files, built for **rebase**.

Most CSV merge tools ask you to configure a key column and fall apart the moment
someone renames a header or reorders anything. `csvmerge` takes the approach git
itself takes: it never looks at just two files. Given the merge ancestor (base),
"ours" and "theirs", it computes the change vector of each side against the
ancestor — for **columns** (added, deleted, renamed, reordered) and for **rows**
(added, deleted, edited, moved) — and combines the two vectors. Changes that
don't compete merge cleanly, down to the individual **cell**: both sides can
edit different columns of the same row.

No key column, no configuration, no schema.

## Conflicts: preserve upstream, put the other value right next door

During a rebase, the merge driver's "ours" (`%A`) is the version **already
committed upstream** — the file that was updated under you — and "theirs"
(`%B`) is your commit being replayed. csvmerge preserves the upstream side as
much as possible:

- On any competing change, the **upstream value stays in the data cells**.
- Every column with a conflict anywhere gets a **companion column right after
  it**, named `<column>_conflict`, holding exactly and only the displaced
  value — raw, ready to copy over the kept value if that's the right call.
- The driver exits `1`, so the rebase stops for review.

The file stays **valid CSV** the whole time. Resolving is pure copy/paste: for
each filled companion cell, either keep the data cell as is or copy the
companion value over it, then delete the conflict column(s) and
`git add` + `git rebase --continue`. No cell editing, no markers.

A stopped rebase looks like this:

```csv
id,name,qty,qty_conflict
1,apple,10,
2,banana,21,22
3,cherry,35,
5,elderberry,50,
4,dragonfruit,40,
```

Upstream set `qty=21`, your replayed commit wanted `22`; everything else merged
clean. Two special forms:

- **Delete vs. edit**: there is no value to copy, so the kept (edited) row is
  flagged `deleted in upstream` / `deleted in rebased` in a trailing row-status
  column named `_conflict`.
- **Competing column renames**: upstream's name is kept in the header; the
  companion is named after the displaced proposal (kept `count`, companion
  `quantity_conflict`), so both candidate names are visible side by side.

Companion names that collide with real data columns are prefixed with `_`
until unique (`_qty_conflict`).

## Content, not bytes

Cell content as parsed by CsvHelper is the unit of comparison, never the raw
bytes. `"apple"` and `apple` are the same value; whitespace outside quotes is
not data (`1, apple` equals `1,apple`); whitespace inside quotes is real
content and survives (it is re-quoted on output so it parses back
identically). A commit that only requotes or repads a file is not a change at
all and can never conflict with anything. Output is written canonically:
fields are quoted only when they need to be.

## How identity is inferred

- **Columns** are matched to the ancestor by exact header name first, then by
  content similarity (so a rename is recognized as a rename, not a
  delete + add), with position only as a tie-break. This holds up under total
  reorganization: every column renamed and reordered at once still maps
  correctly, as long as columns are distinguishable by their values.
- **Rows** are aligned with a patience diff over row content on the columns the
  two versions share. Equal-sized runs of changed rows between the same anchors
  are modifications in place (diff3 chunk semantics); unequal runs are paired
  by cell similarity, so an edited row is recognized as an edit rather than an
  unrelated delete and add. Alignment is order-preserving, and the merged
  output follows "ours" ordering (that's the file the result replaces).
- **Moves** are recognized: a row one side relocated is merged at its new
  position with the other side's edits folded in — so one side can shuffle
  every row while the other side edits cells, and both survive. This includes
  rows that were moved *and edited* in the same commit: exact copies pair
  first, then remaining deletions pair to remaining insertions by cell
  similarity (best matches first), so even "reverse every row and edit every
  one of them" merges against the other side's edits. If the other side
  deleted a moved row, an unedited move loses to the delete; a moved-and-edited
  row becomes a delete/edit conflict flagged at its new position. The heuristic
  boundary: a "moved" row must still share at least half its cells with the
  original — below that it reads as a genuine delete plus an unrelated add.

## Merge semantics

| ours (upstream) \ theirs (rebased) | untouched | edited                        | deleted            |
|-----------------------------------|-----------|-------------------------------|--------------------|
| **untouched**                     | kept      | theirs                        | deleted            |
| **edited**                        | ours      | cell-level merge; competing cells keep ours, theirs' value in `<col>_conflict` | kept + flagged |
| **deleted**                       | deleted   | kept + flagged                | deleted            |

The same table applies to columns (rename = edit of the header cell; a column
deleted on one side but renamed or edited on the other is kept and reported).
Rows inserted by both sides at the same position are deduplicated when
identical, merged-with-annotation when similar (probably the same logical
record), and both kept when unrelated — ours first.

## Install

```
dotnet pack src/CsvMerge -c Release
dotnet tool install --global csvmerge --add-source src/CsvMerge/bin/Release
```

Then register the merge driver (per repo, or `--global` for all repos):

```
csvmerge install [--global]
```

and map CSV files to it in `.gitattributes`:

```
*.csv merge=csvmerge
```

From then on `git rebase`, `git merge`, `git cherry-pick` and `git stash pop`
use csvmerge automatically for `*.csv`.

## Manual use

```
csvmerge <base> <ours> <theirs> [options]

  -o, --output <file>       write the result here instead of over <ours>
      --stdout              print the result instead of writing a file
  -d, --delimiter <str>     field delimiter (default ",")
      --conflict-suffix <s> suffix for the per-column conflict companions, and
                            the name of the row-status column (default
                            "_conflict"; collisions get a "_" prefix)
      --label-ours <s>      label for the ours side (default "upstream")
      --label-theirs <s>    label for the theirs side (default "rebased")
```

Exit codes: `0` clean merge, `1` conflicts, `2` error.

## Notes

- The first record of each file is treated as the header.
- Conflict columns are only added when there is at least one conflict; a
  clean merge adds nothing.
- Output preserves the "ours" file's newline style and UTF-8 BOM.
- Column-structure conflicts (delete vs. modify of a whole column) keep the
  data, count as a conflict, and are explained on stderr.

## Development

Unit tests (merge algorithm, in-process):

```
dotnet test
```

End-to-end scenario suite — 54 self-contained scripts that each build a real
git repo, branch, edit CSVs with sed, commit, and **rebase through the
installed merge driver**, then verify the merged file byte-for-byte (requires
bash + git; on Windows run from Git Bash):

```
dotnet build -c Release
bash tests/e2e/run-tests.sh
```

Each test's expected file is executable documentation of exactly what happens
in that situation: clean cell-level folds, additions/deletions/moves of rows
and columns, renames, reorders, whole-file reorganizations, every conflict
shape and its `_conflict` annotation, quote/whitespace equivalence, CRLF and
BOM preservation, add/add (file created on both branches), the
resolve-and-continue workflow, multi-commit rebases, and the same driver
running under `git cherry-pick`, `git merge`, and `git stash pop` (in each,
the currently checked-out side is the one preserved). A final section
exercises the CLI directly: `--stdout`, `-o`, `--conflict-column`, `install`,
and the 0/1/2 exit codes. `CSVMERGE_E2E_DIR=<path>` keeps the generated repos
for inspection.

Out of scope by design: whole-file renames/deletes (git resolves those before
any merge driver runs) and non-UTF-8 encodings.
