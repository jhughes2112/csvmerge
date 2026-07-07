#!/usr/bin/env bash
# ============================================================================
# End-to-end scenario tests for csvmerge as a git merge driver during rebase.
#
# Every test is a complete, self-contained scenario:
#   1. a fresh git repo is created with csvmerge registered as the merge
#      driver for *.csv,
#   2. a non-trivial base CSV (5 data rows x 6 columns) is committed on main,
#   3. a `feature` branch is created and edited with sed (the commit that will
#      be REPLAYED by the rebase -> "rebased" / theirs / %B),
#   4. main advances with a different sed edit (the version already committed
#      upstream -> "upstream" / ours / %A -- the side csvmerge preserves),
#   5. `git rebase main` runs on feature,
#   6. the merged CSV is compared BYTE FOR BYTE against the expected fold,
#      the rebase exit state is checked, and for conflict cases the
#      resolve -> git add -> rebase --continue workflow is exercised.
#
# The expected file in each test is therefore executable documentation of
# exactly what happens in that situation.
#
# Usage:  bash tests/e2e/run-tests.sh
#         CONFIG=Debug bash tests/e2e/run-tests.sh
#         CSVMERGE_E2E_DIR=/path bash tests/e2e/run-tests.sh   (keep work dirs)
# ============================================================================

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONFIG="${CONFIG:-Release}"
EXE="${CSVMERGE_EXE:-$ROOT/src/CsvMerge/bin/$CONFIG/net10.0/csvmerge.exe}"
[ -f "$EXE" ] || EXE="$ROOT/src/CsvMerge/bin/$CONFIG/net10.0/csvmerge"
[ -f "$EXE" ] || { echo "csvmerge binary not found (set CSVMERGE_EXE or run: dotnet build -c $CONFIG)" >&2; exit 2; }
command -v cygpath >/dev/null 2>&1 && EXE="$(cygpath -m "$EXE")"

WORK="${CSVMERGE_E2E_DIR:-$(mktemp -d)}"
mkdir -p "$WORK"

PASS=0
FAIL=0
FAILURES=()

# ----------------------------------------------------------------- harness --

begin() { # <test-name> <one-line description of the situation and expected outcome>
  TEST="$1"; shift
  DESC="$*"
  FILE="$TEST.csv"
  DIR="$WORK/$TEST"
  LOG="$WORK/$TEST.log"
  rm -rf "$DIR"
  mkdir -p "$DIR"
  cd "$DIR" || exit 2
  git init -q -b main
  git config user.email test@test.invalid
  git config user.name "csvmerge e2e"
  git config core.autocrlf false
  git config advice.mergeConflict false
  git config merge.csvmerge.name "CSV three-way merge driver"
  git config merge.csvmerge.driver "\"$EXE\" %O %A %B"
  printf '*.csv merge=csvmerge\n' > .gitattributes
  TEST_OK=1
  TEST_WHY=""
}

seed()    { cat > "$FILE"; git add -A; git commit -qm "base: seed $FILE"; }
rewrite() { cat > "$FILE"; }
feature() { git checkout -q -b feature; }
main_()   { git checkout -q main; }
commit()  { git add -A; git commit -qm "$1"; }

# sed edit that FAILS the test if it changed nothing (catches typo'd patterns)
edit() {
  local before after
  before="$(cat "$FILE")"
  sed -i "$1" "$FILE"
  after="$(cat "$FILE")"
  [ "$before" != "$after" ] || fail_ "edit had no effect: $1"
}

rebase() {
  git checkout -q feature
  git rebase --empty=drop main > "$LOG" 2>&1
  REBASE=$?
}

continue_rebase() { # resolve step: stage the file and continue
  git add "$FILE"
  GIT_EDITOR=true git rebase --continue >> "$LOG" 2>&1
  REBASE=$?
}

fail_() { TEST_OK=0; TEST_WHY="${TEST_WHY}    - $1"$'\n'; }

expect_clean() {
  [ "$REBASE" -eq 0 ] || fail_ "expected clean rebase, exit=$REBASE: $(tail -n 3 "$LOG" | tr '\n' ' ')"
}

expect_stopped() {
  { [ "$REBASE" -ne 0 ] && grep -q "CONFLICT" "$LOG"; } \
    || fail_ "expected rebase to stop with CONFLICT (exit=$REBASE)"
}

expect_file() { # exact expected content of $FILE on stdin (heredoc)
  local diffout
  if ! diffout="$(diff -u <(cat) "$FILE")"; then
    fail_ "content mismatch (expected vs actual):"$'\n'"$(printf '%s\n' "$diffout" | sed 's/^/      /')"
  fi
}

expect_log() { # rebase/driver output (incl. csvmerge stderr) must contain $1
  grep -q "$1" "$LOG" || fail_ "rebase output missing: '$1'"
}

expect_commits() {
  local n
  n="$(git rev-list --count HEAD)"
  [ "$n" -eq "$1" ] || fail_ "expected $1 commits after rebase, got $n"
}

end() {
  if [ "$TEST_OK" -eq 1 ]; then
    PASS=$((PASS + 1))
    printf 'PASS  %-38s %s\n' "$TEST" "$DESC"
  else
    FAIL=$((FAIL + 1))
    FAILURES+=("$TEST")
    printf 'FAIL  %-38s %s\n%s' "$TEST" "$DESC" "$TEST_WHY"
  fi
  cd "$WORK" || exit 2
}

section() { printf '\n== %s\n' "$*"; }

# The standard 5x6 base grid. Every value is unique so row/column identity is
# unambiguous unless a test deliberately makes it ambiguous.
seed_fruit() { seed <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
}

# ======================================================== clean cell merges ==

t01() {
  begin t01_cell_edits_same_row "both sides edit different columns of the same row -> cell-level clean merge"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,21,/';       commit "feature: banana qty 21"
  main_;   edit 's/^2,banana,20,0.25,/2,banana,20,0.30,/'; commit "upstream: banana price 0.30"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,21,0.30,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t02() {
  begin t02_edits_different_rows "each side edits a different row -> both edits land"
  seed_fruit
  feature; edit 's/^3,cherry,30,/3,cherry,33,/';   commit "feature: cherry qty 33"
  main_;   edit 's/^4,durian,5,8.00,green,/4,durian,5,8.00,brown,/'; commit "upstream: durian color brown"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,33,3.00,red,chile
4,durian,5,8.00,brown,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t03() {
  begin t03_identical_edits "both sides make the same edit -> merges clean (git drops the duplicate commit)"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,25,/'; commit "feature: banana qty 25"
  main_;   edit 's/^2,banana,20,/2,banana,25,/'; commit "upstream: banana qty 25"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,25,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

# ============================================================ row additions ==

t04() {
  begin t04_feature_appends_row "feature appends a row, upstream edits a cell -> both land"
  seed_fruit
  feature; edit '$a 6,fig,15,2.00,brown,turkey';  commit "feature: add fig"
  main_;   edit 's/^1,apple,10,/1,apple,11,/';    commit "upstream: apple qty 11"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,11,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
6,fig,15,2.00,brown,turkey
EOF
  end
}

t05() {
  begin t05_both_append_different_rows "both append unrelated rows at the end -> both kept, upstream's first"
  seed_fruit
  feature; edit '$a 6,fig,15,2.00,brown,turkey';    commit "feature: add fig"
  main_;   edit '$a 7,grape,60,1.10,green,italy';   commit "upstream: add grape"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
7,grape,60,1.10,green,italy
6,fig,15,2.00,brown,turkey
EOF
  end
}

t06() {
  begin t06_both_append_identical_row "both append the exact same row -> deduplicated to one"
  seed_fruit
  feature; edit '$a 6,fig,15,2.00,brown,turkey'; commit "feature: add fig"
  main_;   edit '$a 6,fig,15,2.00,brown,turkey'; commit "upstream: add fig"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
6,fig,15,2.00,brown,turkey
EOF
  end
}

t07() {
  begin t07_both_append_similar_row "both append the same logical row with differing cells -> upstream's kept, each diff in its own companion column"
  seed_fruit
  feature; edit '$a 6,fig,15,2.00,brown,turkey'; commit "feature: add fig (15, turkey)"
  main_;   edit '$a 6,fig,18,2.00,brown,greece'; commit "upstream: add fig (18, greece)"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin,origin_conflict
1,apple,10,,0.50,red,peru,
2,banana,20,,0.25,yellow,ecuador,
3,cherry,30,,3.00,red,chile,
4,durian,5,,8.00,green,malaysia,
5,elderberry,50,,4.50,purple,france,
6,fig,18,15,2.00,brown,greece,turkey
EOF
  end
}

t08() {
  begin t08_insert_row_middle "feature inserts a row mid-file, upstream edits elsewhere -> position preserved"
  seed_fruit
  feature; edit '/^2,banana/a 25,blueberry,40,6.00,blue,canada'; commit "feature: insert blueberry"
  main_;   edit 's/^5,elderberry,50,/5,elderberry,55,/';         commit "upstream: elderberry qty 55"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
25,blueberry,40,6.00,blue,canada
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,55,4.50,purple,france
EOF
  end
}

# ============================================================ row deletions ==

t09() {
  begin t09_feature_deletes_row "feature deletes a row upstream didn't touch -> row removed"
  seed_fruit
  feature; edit '/^3,cherry/d';               commit "feature: delete cherry"
  main_;   edit 's/^1,apple,10,/1,apple,11,/'; commit "upstream: apple qty 11"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,11,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t10() {
  begin t10_upstream_deletes_row "upstream deletes a row feature didn't touch -> row removed"
  seed_fruit
  feature; edit 's/^1,apple,10,/1,apple,11,/'; commit "feature: apple qty 11"
  main_;   edit '/^3,cherry/d';                commit "upstream: delete cherry"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,11,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t11() {
  begin t11_both_delete_row "both delete the same row -> row removed, no conflict"
  seed_fruit
  feature; edit '/^3,cherry/d'; commit "feature: delete cherry"
  main_;   edit '/^3,cherry/d'; commit "upstream: delete cherry"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t12() {
  begin t12_upstream_deletes_feature_edits "upstream deleted the row feature edited -> row kept with feature's edit, annotated 'deleted in upstream'"
  seed_fruit
  feature; edit 's/^3,cherry,30,/3,cherry,33,/'; commit "feature: cherry qty 33"
  main_;   edit '/^3,cherry/d';                  commit "upstream: delete cherry"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,price,color,origin,_conflict
1,apple,10,0.50,red,peru,
2,banana,20,0.25,yellow,ecuador,
3,cherry,33,3.00,red,chile,deleted in upstream
4,durian,5,8.00,green,malaysia,
5,elderberry,50,4.50,purple,france,
EOF
  end
}

t13() {
  begin t13_upstream_edits_feature_deletes "feature deleted the row upstream edited -> row kept with upstream's edit, annotated 'deleted in rebased'"
  seed_fruit
  feature; edit '/^3,cherry/d';                  commit "feature: delete cherry"
  main_;   edit 's/^3,cherry,30,/3,cherry,35,/'; commit "upstream: cherry qty 35"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,price,color,origin,_conflict
1,apple,10,0.50,red,peru,
2,banana,20,0.25,yellow,ecuador,
3,cherry,35,3.00,red,chile,deleted in rebased
4,durian,5,8.00,green,malaysia,
5,elderberry,50,4.50,purple,france,
EOF
  end
}

# ============================================================ cell conflicts ==

t14() {
  begin t14_same_cell_conflict "both edit the same cell differently -> upstream value kept, rebased value in qty_conflict"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

t15() {
  begin t15_resolve_and_continue "conflict resolution workflow: pick rebased value, drop the column, add, rebase --continue"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_stopped
  rewrite <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  continue_rebase; expect_clean
  expect_commits 3
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t16() {
  begin t16_multi_cell_conflict_same_row "two cells of one row conflict -> each conflicted column gets its own companion"
  seed_fruit
  feature; edit 's/^2,banana,20,0.25,yellow,/2,banana,22,0.25,brown,/'; commit "feature: qty 22, color brown"
  main_;   edit 's/^2,banana,20,0.25,yellow,/2,banana,21,0.25,green,/'; commit "upstream: qty 21, color green"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,color_conflict,origin
1,apple,10,,0.50,red,,peru
2,banana,21,22,0.25,green,brown,ecuador
3,cherry,30,,3.00,red,,chile
4,durian,5,,8.00,green,,malaysia
5,elderberry,50,,4.50,purple,,france
EOF
  end
}

t17() {
  begin t17_conflict_plus_clean_edit_same_row "one cell conflicts, another cell only feature edited -> clean edit merges, conflict in its companion"
  seed_fruit
  feature; edit 's/^2,banana,20,0.25,/2,banana,22,0.35,/'; commit "feature: qty 22, price 0.35"
  main_;   edit 's/^2,banana,20,/2,banana,21,/';           commit "upstream: qty 21"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.35,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

t18() {
  begin t18_conflicts_multiple_rows "two different rows conflict in the same column -> one companion column serves both rows"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,22,/;s/^4,durian,5,/4,durian,8,/'; commit "feature: banana 22, durian 8"
  main_;   edit 's/^2,banana,20,/2,banana,21,/;s/^4,durian,5,/4,durian,7,/'; commit "upstream: banana 21, durian 7"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,7,8,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

# ========================================================== column additions ==

t19() {
  begin t19_feature_adds_column "feature adds a column, upstream edits a cell -> column appended, edit merges"
  seed_fruit
  feature; edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/'; commit "feature: add stock column"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin,stock
1,apple,10,0.50,red,peru,yes
2,banana,21,0.25,yellow,ecuador,no
3,cherry,30,3.00,red,chile,yes
4,durian,5,8.00,green,malaysia,no
5,elderberry,50,4.50,purple,france,yes
EOF
  end
}

t20() {
  begin t20_upstream_adds_column_feature_adds_row "upstream adds a column, feature adds a row -> new row gets an empty cell in the new column"
  seed_fruit
  feature; edit '$a 6,fig,15,2.00,brown,turkey'; commit "feature: add fig"
  main_;   edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/'; commit "upstream: add stock column"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin,stock
1,apple,10,0.50,red,peru,yes
2,banana,20,0.25,yellow,ecuador,no
3,cherry,30,3.00,red,chile,yes
4,durian,5,8.00,green,malaysia,no
5,elderberry,50,4.50,purple,france,yes
6,fig,15,2.00,brown,turkey,
EOF
  end
}

t21() {
  begin t21_both_add_same_column_same_values "both add the same column with the same values -> one column, no conflict"
  seed_fruit
  feature; edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/'; commit "feature: add stock"
  main_;   edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/'; commit "upstream: add stock"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin,stock
1,apple,10,0.50,red,peru,yes
2,banana,20,0.25,yellow,ecuador,no
3,cherry,30,3.00,red,chile,yes
4,durian,5,8.00,green,malaysia,no
5,elderberry,50,4.50,purple,france,yes
EOF
  end
}

t22() {
  begin t22_both_add_same_column_one_differing_cell "both add the same column but one value differs -> upstream's kept, other in stock_conflict"
  seed_fruit
  feature; edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,maybe/;5s/$/,no/;6s/$/,yes/'; commit "feature: add stock (cherry=maybe)"
  main_;   edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/';   commit "upstream: add stock (cherry=yes)"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,price,color,origin,stock,stock_conflict
1,apple,10,0.50,red,peru,yes,
2,banana,20,0.25,yellow,ecuador,no,
3,cherry,30,3.00,red,chile,yes,maybe
4,durian,5,8.00,green,malaysia,no,
5,elderberry,50,4.50,purple,france,yes,
EOF
  end
}

# ========================================================== column deletions ==

t23() {
  begin t23_feature_deletes_column "feature deletes a column upstream didn't touch -> column removed"
  seed_fruit
  feature; edit 's/,[^,]*$//';                   commit "feature: delete origin column"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color
1,apple,10,0.50,red
2,banana,21,0.25,yellow
3,cherry,30,3.00,red
4,durian,5,8.00,green
5,elderberry,50,4.50,purple
EOF
  end
}

t24() {
  begin t24_upstream_deletes_column_feature_edits_it "upstream deleted the column feature edited -> column kept with feature's edit, conflict on stderr"
  seed_fruit
  feature; edit 's/,ecuador$/,colombia/'; commit "feature: banana origin colombia"
  main_;   edit 's/,[^,]*$//';            commit "upstream: delete origin column"
  rebase; expect_stopped
  expect_log "deleted in ours but modified in theirs"
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,colombia
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t25() {
  begin t25_feature_deletes_column_upstream_edits_it "feature deleted the column upstream edited -> column kept with upstream's edit, conflict on stderr"
  seed_fruit
  feature; edit 's/,[^,]*$//';            commit "feature: delete origin column"
  main_;   edit 's/,ecuador$/,colombia/'; commit "upstream: banana origin colombia"
  rebase; expect_stopped
  expect_log "deleted in theirs but modified in ours"
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,colombia
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

# =========================================================== column renames ==

t26() {
  begin t26_feature_renames_column "feature renames a column, upstream edits data in it -> rename respected, edit merges"
  seed_fruit
  feature; edit '1s/,qty,/,quantity,/';          commit "feature: rename qty -> quantity"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,quantity,price,color,origin
1,apple,10,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t27() {
  begin t27_both_rename_column_same "both rename the same column identically -> one rename, no conflict"
  seed_fruit
  feature; edit '1s/,qty,/,quantity,/'; commit "feature: rename qty"
  main_;   edit '1s/,qty,/,quantity,/'; commit "upstream: rename qty"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,quantity,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t28() {
  begin t28_competing_column_renames "both rename the same column differently -> upstream's name kept, companion named after theirs' proposal"
  seed_fruit
  feature; edit '1s/,qty,/,quantity,/'; commit "feature: rename qty -> quantity"
  main_;   edit '1s/,qty,/,count,/';    commit "upstream: rename qty -> count"
  rebase; expect_stopped
  expect_log "renamed to 'count' in ours and 'quantity' in theirs"
  expect_file <<'EOF'
id,name,count,quantity_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,20,,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

# =========================================================== reorders/moves ==

t29() {
  begin t29_upstream_reorders_columns "upstream reorders columns, feature edits a cell -> upstream's order wins, edit merges"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,21,/'; commit "feature: banana qty 21"
  main_
  awk -F, 'BEGIN{OFS=","}{print $2,$1,$3,$4,$5,$6}' "$FILE" > reordered.tmp && mv reordered.tmp "$FILE"
  commit "upstream: name column first"
  rebase; expect_clean
  expect_file <<'EOF'
name,id,qty,price,color,origin
apple,1,10,0.50,red,peru
banana,2,21,0.25,yellow,ecuador
cherry,3,30,3.00,red,chile
durian,4,5,8.00,green,malaysia
elderberry,5,50,4.50,purple,france
EOF
  end
}

t30() {
  begin t30_feature_moves_row "feature moves a row upstream didn't touch -> move respected"
  seed_fruit
  feature; edit '/^5,elderberry/d;/^3,cherry/i 5,elderberry,50,4.50,purple,france'; commit "feature: move elderberry above cherry"
  main_;   edit 's/^1,apple,10,/1,apple,11,/'; commit "upstream: apple qty 11"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,11,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
5,elderberry,50,4.50,purple,france
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
EOF
  end
}

t31() {
  begin t31_move_vs_edit "feature moves the row upstream edited -> move respected, the edit follows the row"
  seed_fruit
  feature; edit '/^5,elderberry/d;/^3,cherry/i 5,elderberry,50,4.50,purple,france'; commit "feature: move elderberry"
  main_;   edit 's/^5,elderberry,50,/5,elderberry,55,/'; commit "upstream: elderberry qty 55"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
5,elderberry,55,4.50,purple,france
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
EOF
  end
}

# =========================================================== CSV edge cases ==

t32() {
  begin t32_quoted_fields "quoted fields with embedded commas survive; quoting is canonicalized (only where needed)"
  seed <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,"kiwi, golden",20,0.25,green,"new zealand"
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  feature; edit 's/^2,"kiwi, golden",20,/2,"kiwi, golden",21,/'; commit "feature: kiwi qty 21"
  main_;   edit 's/^3,cherry,30,/3,cherry,31,/';                 commit "upstream: cherry qty 31"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,"kiwi, golden",21,0.25,green,new zealand
3,cherry,31,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t33() {
  begin t33_conflict_value_contains_delimiter "the displaced conflicting value contains the delimiter -> companion cell is quoted"
  seed_fruit
  feature; edit 's/^2,banana,/2,"ba,nana",/'; commit "feature: rename banana -> ba,nana"
  main_;   edit 's/^2,banana,/2,banana2,/';   commit "upstream: rename banana -> banana2"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,name_conflict,qty,price,color,origin
1,apple,,10,0.50,red,peru
2,banana2,"ba,nana",20,0.25,yellow,ecuador
3,cherry,,30,3.00,red,chile
4,durian,,5,8.00,green,malaysia
5,elderberry,,50,4.50,purple,france
EOF
  end
}

t34() {
  begin t34_conflict_column_name_collision "data already has a qty_conflict column -> companion is uniquified to _qty_conflict"
  seed <<'EOF'
id,name,qty,qty_conflict,price
1,apple,10,a,0.50
2,banana,20,b,0.25
3,cherry,30,c,3.00
4,durian,5,d,8.00
5,elderberry,50,e,4.50
EOF
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,_qty_conflict,qty_conflict,price
1,apple,10,,a,0.50
2,banana,21,22,b,0.25
3,cherry,30,,c,3.00
4,durian,5,,d,8.00
5,elderberry,50,,e,4.50
EOF
  end
}

# ==================================================== semantic equivalence ==

t39() {
  begin t39_quote_only_changes "feature requotes every field (no content change) -> not a change at all; upstream wins untouched"
  seed_fruit
  feature
  rewrite <<'EOF'
"id","name","qty","price","color","origin"
"1","apple","10","0.50","red","peru"
"2","banana","20","0.25","yellow","ecuador"
"3","cherry","30","3.00","red","chile"
"4","durian","5","8.00","green","malaysia"
"5","elderberry","50","4.50","purple","france"
EOF
  commit "feature: requote everything"
  main_; edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t40() {
  begin t40_whitespace_only_changes "feature pads every delimiter with spaces (no content change) -> not a change; no conflict with upstream's edit"
  seed_fruit
  feature; edit 's/,/,   /g'; commit "feature: pad all delimiters"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

# ==================================================== big reorganizations ==

t41() {
  begin t41_all_columns_renamed "feature renames EVERY column, upstream edits data -> content matching maps them all, edits merge"
  seed_fruit
  feature; edit '1s/.*/ID,NAME,QTY,PRICE,COLOR,ORIGIN/'; commit "feature: rename all columns"
  main_
  edit 's/^2,banana,20,/2,banana,21,/'
  edit 's/^4,durian,5,8.00,green,/4,durian,5,8.00,brown,/'
  commit "upstream: banana qty 21, durian brown"
  rebase; expect_clean
  expect_file <<'EOF'
ID,NAME,QTY,PRICE,COLOR,ORIGIN
1,apple,10,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,brown,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

t42() {
  begin t42_all_columns_renamed_and_shuffled "upstream renames AND reverses every column, feature edits a cell -> full remap, upstream layout wins"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,21,/'; commit "feature: banana qty 21"
  main_
  awk -F, 'BEGIN{OFS=","}{print $6,$5,$4,$3,$2,$1}' "$FILE" > reordered.tmp && mv reordered.tmp "$FILE"
  edit '1s/.*/src,hue,cost,count,title,key/'
  commit "upstream: reverse and rename all columns"
  rebase; expect_clean
  expect_file <<'EOF'
src,hue,cost,count,title,key
peru,red,0.50,10,apple,1
ecuador,yellow,0.25,21,banana,2
chile,red,3.00,30,cherry,3
malaysia,green,8.00,5,durian,4
france,purple,4.50,50,elderberry,5
EOF
  end
}

t43() {
  begin t43_rows_shuffled_in_feature "feature reverses ALL rows, upstream edits two of them -> feature's order, upstream's edits follow the rows"
  seed_fruit
  feature
  awk 'NR==1{print;next}{a[NR]=$0}END{for(i=NR;i>1;i--)print a[i]}' "$FILE" > shuffled.tmp && mv shuffled.tmp "$FILE"
  commit "feature: reverse all rows"
  main_
  edit 's/^2,banana,20,/2,banana,21,/'
  edit 's/^4,durian,5,8.00,green,/4,durian,5,8.00,brown,/'
  commit "upstream: banana qty 21, durian brown"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
5,elderberry,50,4.50,purple,france
4,durian,5,8.00,brown,malaysia
3,cherry,30,3.00,red,chile
2,banana,21,0.25,yellow,ecuador
1,apple,10,0.50,red,peru
EOF
  end
}

t44() {
  begin t44_rows_shuffled_in_upstream "upstream reverses ALL rows, feature edits two of them -> upstream's order, feature's edits follow the rows"
  seed_fruit
  feature
  edit 's/^2,banana,20,/2,banana,22,/'
  edit 's/^3,cherry,30,/3,cherry,33,/'
  commit "feature: banana 22, cherry 33"
  main_
  awk 'NR==1{print;next}{a[NR]=$0}END{for(i=NR;i>1;i--)print a[i]}' "$FILE" > shuffled.tmp && mv shuffled.tmp "$FILE"
  commit "upstream: reverse all rows"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
5,elderberry,50,4.50,purple,france
4,durian,5,8.00,green,malaysia
3,cherry,33,3.00,red,chile
2,banana,22,0.25,yellow,ecuador
1,apple,10,0.50,red,peru
EOF
  end
}

t45() {
  begin t45_move_and_edit_same_side "feature moves AND edits the row upstream also edited -> both changes merge at the new position"
  seed_fruit
  feature; edit '/^5,elderberry/d;/^3,cherry/i 5,elderberry,51,4.50,purple,france'; commit "feature: move elderberry and set qty 51"
  main_;   edit 's/^5,elderberry,50,4.50,/5,elderberry,50,4.60,/'; commit "upstream: elderberry price 4.60"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
5,elderberry,51,4.60,purple,france
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
EOF
  end
}

t55() {
  begin t55_move_and_edit_vs_delete "feature moves AND edits the row upstream deleted -> delete/edit conflict flagged at the new position"
  seed_fruit
  feature; edit '/^5,elderberry/d;/^3,cherry/i 5,elderberry,51,4.50,purple,france'; commit "feature: move elderberry and set qty 51"
  main_;   edit '/^5,elderberry/d'; commit "upstream: delete elderberry"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,price,color,origin,_conflict
1,apple,10,0.50,red,peru,
2,banana,20,0.25,yellow,ecuador,
5,elderberry,51,4.50,purple,france,deleted in upstream
3,cherry,30,3.00,red,chile,
4,durian,5,8.00,green,malaysia,
EOF
  end
}

t56() {
  begin t56_shuffle_and_edit_everything "feature reverses ALL rows and edits EVERY qty, upstream edits other columns -> all of it folds cleanly"
  seed_fruit
  feature
  awk -F, 'BEGIN{OFS=","} NR==1{print;next}{$3=$3+1;a[NR]=$0} END{for(i=NR;i>1;i--)print a[i]}' "$FILE" > shuffled.tmp && mv shuffled.tmp "$FILE"
  commit "feature: reverse all rows, bump every qty"
  main_
  edit 's/^2,banana,20,0.25,yellow,/2,banana,20,0.25,gold,/'
  edit 's/^3,cherry,30,3.00,red,chile/3,cherry,30,3.00,red,peru/'
  commit "upstream: banana color gold, cherry origin peru"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
5,elderberry,51,4.50,purple,france
4,durian,6,8.00,green,malaysia
3,cherry,31,3.00,red,peru
2,banana,21,0.25,gold,ecuador
1,apple,11,0.50,red,peru
EOF
  end
}

# ======================================================= multi-commit rebase ==

t35() {
  begin t35_multi_commit_feature_clean "feature has two commits, both replay cleanly -> history preserved"
  seed_fruit
  feature
  edit 's/^1,apple,10,/1,apple,11,/';       commit "feature 1/2: apple qty 11"
  edit '$a 6,fig,15,2.00,brown,turkey';     commit "feature 2/2: add fig"
  main_; edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_clean
  expect_commits 4
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,11,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
6,fig,15,2.00,brown,turkey
EOF
  end
}

t36() {
  begin t36_multi_commit_conflict_in_second "feature's first commit replays clean, second conflicts -> stop shows both; resolve and continue"
  seed_fruit
  feature
  edit 's/^4,durian,5,/4,durian,6,/';        commit "feature 1/2: durian qty 6"
  edit 's/^2,banana,20,/2,banana,22,/';      commit "feature 2/2: banana qty 22"
  main_; edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,6,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  rewrite <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,6,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  continue_rebase; expect_clean
  expect_commits 4
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,6,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

# ============================================================= combinations ==

t37() {
  begin t37_kitchen_sink "rename+edit+append upstream vs add-column+delete-row+edit feature -> everything folds cleanly"
  seed_fruit
  feature
  edit '1s/$/,stock/;2s/$/,yes/;3s/$/,no/;4s/$/,yes/;5s/$/,no/;6s/$/,yes/'
  edit '/^4,durian/d'
  edit 's/,red,peru/,pink,peru/'
  commit "feature: add stock, delete durian, apple color pink"
  main_
  edit '1s/,qty,/,count,/'
  edit 's/^1,apple,10,0.50,/1,apple,10,0.55,/'
  edit '$a 7,grape,60,1.10,green,italy'
  commit "upstream: rename qty->count, apple price 0.55, add grape"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,count,price,color,origin,stock
1,apple,10,0.55,pink,peru,yes
2,banana,20,0.25,yellow,ecuador,no
3,cherry,30,3.00,red,chile,yes
5,elderberry,50,4.50,purple,france,yes
7,grape,60,1.10,green,italy,
EOF
  end
}

t38() {
  begin t38_full_row_replacement_both_sides "both replace the same row entirely (diff3 chunk rule) -> per-cell conflicts, upstream kept"
  seed_fruit
  feature; edit '/^3,cherry/c 3,cranberry,90,2.20,red,usa';  commit "feature: cherry -> cranberry"
  main_;   edit '/^3,cherry/c 3,coconut,1,9.00,white,fiji';  commit "upstream: cherry -> coconut"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,name_conflict,qty,qty_conflict,price,price_conflict,color,origin,origin_conflict
1,apple,,10,,0.50,,red,peru,
2,banana,,20,,0.25,,yellow,ecuador,
3,coconut,cranberry,1,90,9.00,2.20,white,fiji,usa
4,durian,,5,,8.00,,green,malaysia,
5,elderberry,,50,,4.50,,purple,france,
EOF
  end
}

# ========================================================== file formats ==

t46() {
  begin t46_add_add_empty_base "file created on BOTH branches (no ancestor) -> rows merge, identical rows dedupe, differing cells annotate"
  git add -A; git commit -qm "base: no csv yet"
  feature
  rewrite <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  commit "feature: create file (banana 22)"
  main_
  rewrite <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,21,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  commit "upstream: create file (banana 21)"
  rebase; expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

t47() {
  begin t47_crlf_preserved "CRLF line endings in the upstream file are preserved through the merge"
  sed 's/$/\r/' > "$FILE" <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  git add -A; git commit -qm "base: CRLF file"
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^3,cherry,30,/3,cherry,31,/'; commit "upstream: cherry qty 31"
  rebase; expect_clean
  [ "$(grep -c $'\r' "$FILE")" -eq 6 ] || fail_ "expected 6 CRLF line endings, got $(grep -c $'\r' "$FILE")"
  diff -u <(cat <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,31,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
) <(tr -d '\r' < "$FILE") > /dev/null || fail_ "content mismatch after CRLF merge"
  end
}

t48() {
  begin t48_bom_preserved "a UTF-8 BOM on the upstream file is preserved and doesn't corrupt the first header name"
  printf '\xef\xbb\xbf' > "$FILE"
  cat >> "$FILE" <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,20,0.25,yellow,ecuador
3,cherry,30,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  git add -A; git commit -qm "base: BOM file"
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^3,cherry,30,/3,cherry,31,/'; commit "upstream: cherry qty 31"
  rebase; expect_clean
  [ "$(head -c 3 "$FILE" | od -An -tx1 | tr -d ' \n')" = "efbbbf" ] || fail_ "UTF-8 BOM was not preserved"
  grep -q '^id,name' <(tail -c +4 "$FILE") || fail_ "header corrupted after BOM handling"
  grep -q '2,banana,22' "$FILE" || fail_ "feature edit missing"
  grep -q '3,cherry,31' "$FILE" || fail_ "upstream edit missing"
  end
}

t49() {
  begin t49_no_trailing_newline "a file without a trailing newline merges fine; output is normalized to end with one"
  printf 'id,name,qty,price,color,origin\n1,apple,10,0.50,red,peru\n2,banana,20,0.25,yellow,ecuador\n3,cherry,30,3.00,red,chile\n4,durian,5,8.00,green,malaysia\n5,elderberry,50,4.50,purple,france' > "$FILE"
  git add -A; git commit -qm "base: no trailing newline"
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^3,cherry,30,/3,cherry,31,/'; commit "upstream: cherry qty 31"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,price,color,origin
1,apple,10,0.50,red,peru
2,banana,22,0.25,yellow,ecuador
3,cherry,31,3.00,red,chile
4,durian,5,8.00,green,malaysia
5,elderberry,50,4.50,purple,france
EOF
  end
}

# ====================================================== other git commands ==

t50() {
  begin t50_cherry_pick "git cherry-pick uses the driver: current branch preserved, picked commit's value annotated"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  git cherry-pick feature > "$LOG" 2>&1
  REBASE=$?
  expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  git cherry-pick --abort
  end
}

t51() {
  begin t51_plain_merge "git merge uses the driver: the current (checked-out) branch is the preserved side"
  seed_fruit
  feature; edit 's/^2,banana,20,/2,banana,22,/'; commit "feature: banana qty 22"
  main_;   edit 's/^2,banana,20,/2,banana,21,/'; commit "main: banana qty 21"
  git merge feature > "$LOG" 2>&1
  REBASE=$?
  expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  git merge --abort
  end
}

t52() {
  begin t52_stash_pop "git stash pop uses the driver: committed value preserved, stashed value annotated"
  seed_fruit
  edit 's/^2,banana,20,/2,banana,22,/'
  git stash -q
  edit 's/^2,banana,20,/2,banana,21,/'; commit "upstream: banana qty 21"
  git stash pop > "$LOG" 2>&1
  REBASE=$?
  expect_stopped
  expect_file <<'EOF'
id,name,qty,qty_conflict,price,color,origin
1,apple,10,,0.50,red,peru
2,banana,21,22,0.25,yellow,ecuador
3,cherry,30,,3.00,red,chile
4,durian,5,,8.00,green,malaysia
5,elderberry,50,,4.50,purple,france
EOF
  end
}

# ================================================================ CLI direct ==

t53() {
  begin t53_cli_direct "direct CLI invocation: --stdout, -o, --conflict-suffix, and exit codes 0/1/2"
  cat > base.csv <<'EOF'
id,name,qty
1,apple,10
2,banana,20
3,cherry,30
EOF
  sed 's/^2,banana,20/2,banana,21/' base.csv > ours.csv
  sed 's/^2,banana,20/2,banana,22/' base.csv > theirs.csv
  sed 's/^3,cherry,30/3,cherry,33/' base.csv > clean.csv

  "$EXE" base.csv ours.csv theirs.csv --stdout > out.csv 2> err.txt
  [ $? -eq 1 ] || fail_ "conflicting merge should exit 1"
  grep -q '^id,name,qty,qty_conflict$' out.csv || fail_ "--stdout output missing companion column"
  grep -q '^2,banana,21,22$' out.csv || fail_ "--stdout output missing conflicted row"
  grep -q '^2,banana,21$' ours.csv || fail_ "--stdout must not modify ours.csv"

  "$EXE" base.csv ours.csv theirs.csv -o merged.csv --conflict-suffix _NOTE > /dev/null 2>&1
  [ $? -eq 1 ] || fail_ "-o conflicting merge should exit 1"
  grep -q '^id,name,qty,qty_NOTE$' merged.csv || fail_ "--conflict-suffix not used"
  grep -q '^2,banana,21$' ours.csv || fail_ "-o must not modify ours.csv"

  "$EXE" base.csv ours.csv clean.csv -o cleanmerge.csv > /dev/null 2>&1
  [ $? -eq 0 ] || fail_ "clean merge should exit 0"
  grep -q '2,banana,21' cleanmerge.csv && grep -q '3,cherry,33' cleanmerge.csv \
    || fail_ "clean merge content wrong"

  "$EXE" base.csv missing.csv theirs.csv --stdout > /dev/null 2>&1
  [ $? -eq 2 ] || fail_ "missing input file should exit 2"

  "$EXE" > /dev/null 2>&1
  [ $? -eq 2 ] || fail_ "no arguments should exit 2"
  end
}

t54() {
  begin t54_install_command "csvmerge install registers both drivers in local config AND maps *.csv in .gitattributes"
  "$EXE" install > /dev/null 2>&1 || fail_ "install exited nonzero"
  [ "$(git config merge.csvmerge.driver)" = "csvmerge %O %A %B" ] || fail_ "merge driver config not written"
  [ -n "$(git config merge.csvmerge.name)" ] || fail_ "merge driver name not written"
  [ "$(git config diff.csvmerge.command)" = "csvmerge gitdiff" ] || fail_ "diff driver config not written"
  grep -q '^\*\.csv merge=csvmerge diff=csvmerge$' .gitattributes || fail_ "mapping not added to .gitattributes"
  "$EXE" install > /dev/null 2>&1 || fail_ "second install exited nonzero"
  [ "$(grep -c 'diff=csvmerge' .gitattributes)" -eq 1 ] || fail_ "second install duplicated the mapping"
  end
}

t60() {
  begin t60_install_global "install --global maps *.csv in the global attributes file (sandboxed HOME)"
  mkdir -p home
  HOME="$PWD/home" XDG_CONFIG_HOME="$PWD/home/.config" "$EXE" install --global > /dev/null 2>&1 \
    || fail_ "install --global exited nonzero"
  [ "$(HOME="$PWD/home" XDG_CONFIG_HOME="$PWD/home/.config" git config --global merge.csvmerge.driver)" = "csvmerge %O %A %B" ] \
    || fail_ "global merge driver config not written"
  ATTRS="$PWD/home/.config/git/attributes"
  grep -q '^\*\.csv merge=csvmerge diff=csvmerge$' "$ATTRS" 2>/dev/null || fail_ "mapping not in global attributes file"
  HOME="$PWD/home" XDG_CONFIG_HOME="$PWD/home/.config" "$EXE" install --global > /dev/null 2>&1
  [ "$(grep -c 'diff=csvmerge' "$ATTRS")" -eq 1 ] || fail_ "second install duplicated the mapping"
  end
}

# ============================================================ semantic diff ==

t57() {
  begin t57_git_semantic_diff "git diff uses the semantic driver: cell changes and moves instead of line noise"
  git config diff.csvmerge.command "\"$EXE\" gitdiff"
  printf '*.csv merge=csvmerge diff=csvmerge\n' > .gitattributes
  seed_fruit
  edit 's/^2,banana,20,/2,banana,21,/'
  edit '/^5,elderberry/d;/^3,cherry/i 5,elderberry,50,4.50,purple,france'
  git diff > "$LOG" 2>&1
  grep -q "csvdiff a/$FILE b/$FILE" "$LOG" || fail_ "missing csvdiff header"
  grep -q '~ row \[id=2\]: qty: 20 -> 21' "$LOG" || fail_ "missing cell change line"
  grep -q '> row \[id=5\] moved (5 -> 3)' "$LOG" || fail_ "missing move line"
  grep -q '^@@' "$LOG" && fail_ "raw unified-diff hunks leaked through"
  end
}

t58() {
  begin t58_diff_cli_direct "csvmerge diff: exit 0 identical (even requoted), exit 1 with cell-level output on change"
  cat > old.csv <<'EOF'
id,name,qty
1,apple,10
2,banana,20
3,cherry,30
EOF
  printf '"id","name","qty"\n"1","apple","10"\n"2","banana","20"\n"3","cherry","30"\n' > requoted.csv
  sed 's/^2,banana,20/2,banana,25/' old.csv > edited.csv

  "$EXE" diff old.csv requoted.csv > /dev/null 2>&1
  [ $? -eq 0 ] || fail_ "requote-only diff should exit 0"

  "$EXE" diff old.csv edited.csv > diff.out 2>&1
  [ $? -eq 1 ] || fail_ "changed file should exit 1"
  grep -q '~ row \[id=2\]: qty: 20 -> 25' diff.out || fail_ "missing semantic change line"
  end
}

# ========================================================= weighted identity ==

t59() {
  begin t59_weighted_identity "constant columns don't fake identity: move+heavy-edit tracked by distinctive id/name through a real rebase"
  seed <<'EOF'
id,name,qty,status,region
1,apple,10,active,us
2,banana,20,active,us
3,cherry,30,active,us
4,durian,40,active,us
5,elderberry,50,active,us
EOF
  feature; edit '/^3,cherry/d;1a 3,cherry,31,inactive,eu'; commit "feature: move cherry to top, edit qty+status+region"
  main_;   edit 's/^3,cherry,/3,cherries,/'; commit "upstream: rename cherry -> cherries"
  rebase; expect_clean
  expect_file <<'EOF'
id,name,qty,status,region
3,cherries,31,inactive,eu
1,apple,10,active,us
2,banana,20,active,us
4,durian,40,active,us
5,elderberry,50,active,us
EOF
  end
}

# ------------------------------------------------------------------- runner --

echo "csvmerge end-to-end rebase scenarios"
echo "driver: $EXE"
echo "work:   $WORK"

section "clean cell merges"
t01; t02; t03
section "row additions"
t04; t05; t06; t07; t08
section "row deletions"
t09; t10; t11; t12; t13
section "cell conflicts"
t14; t15; t16; t17; t18
section "column additions"
t19; t20; t21; t22
section "column deletions"
t23; t24; t25
section "column renames"
t26; t27; t28
section "reorders and moves"
t29; t30; t31
section "CSV edge cases"
t32; t33; t34
section "semantic equivalence (quotes and whitespace)"
t39; t40
section "big reorganizations"
t41; t42; t43; t44; t45; t55; t56
section "multi-commit rebases"
t35; t36
section "combinations"
t37; t38
section "file formats"
t46; t47; t48; t49
section "other git commands"
t50; t51; t52
section "CLI direct"
t53; t54; t60
section "semantic diff"
t57; t58
section "weighted identity"
t59

echo
echo "passed $PASS, failed $FAIL, total $((PASS + FAIL))"
if [ "$FAIL" -ne 0 ]; then
  echo "failed tests: ${FAILURES[*]}"
  exit 1
fi
