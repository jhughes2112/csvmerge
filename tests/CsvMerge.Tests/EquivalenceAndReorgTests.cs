using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

/// <summary>
/// Semantic equivalence (cell content per CsvHelper is the truth, not bytes)
/// and extreme reorganizations: every column renamed and/or shuffled, whole
/// row sets shuffled on one side while the other side edits.
/// </summary>
public class EquivalenceAndReorgTests
{
    private static (string Text, MergeResult Result) Merge(string baseCsv, string oursCsv, string theirsCsv)
    {
        var result = ThreeWayMerger.Merge(
            CsvTable.Parse(baseCsv, ","),
            CsvTable.Parse(oursCsv, ","),
            CsvTable.Parse(theirsCsv, ","),
            "upstream", "rebased");
        var text = MergeWriter.Write(result, ",", "\n");
        return (text, result);
    }

    private const string Base =
        "id,name,qty\n" +
        "1,apple,10\n" +
        "2,banana,20\n" +
        "3,cherry,30\n";

    // ------------------------------------------------ semantic equivalence --

    [Fact]
    public void QuoteOnlyDifferencesAreNotChanges()
    {
        const string requoted =
            "\"id\",\"name\",\"qty\"\n" +
            "\"1\",\"apple\",\"10\"\n" +
            "\"2\",\"banana\",\"20\"\n" +
            "\"3\",\"cherry\",\"30\"\n";
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, requoted, theirs);
        Assert.Equal(0, result.ConflictCount);
        // ours' requoting is a no-op; theirs' edit lands; output is canonical
        Assert.Equal(Base.Replace("2,banana,20", "2,banana,25"), text);
    }

    [Fact]
    public void WhitespaceOutsideQuotesIsNotAChange()
    {
        var padded = Base.Replace(",", ",   ");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, padded, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(Base.Replace("2,banana,20", "2,banana,25"), text);
    }

    [Fact]
    public void QuoteAndWhitespaceChangesDoNotConflictWithRealEdits()
    {
        // ours requotes and pads the very row theirs edits: still no conflict
        var ours = Base.Replace("2,banana,20", "\"2\" , \"banana\" , \"20\"");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("2,banana,25", text);
    }

    [Fact]
    public void WhitespaceInsideQuotesIsRealContentAndRoundTrips()
    {
        const string baseCsv =
            "id,note,qty\n" +
            "1,\"  padded  \",10\n" +
            "2,plain,20\n" +
            "3,other,30\n";
        var theirs = baseCsv.Replace("2,plain,20", "2,plain,25");
        var (text, result) = Merge(baseCsv, baseCsv, theirs);
        Assert.Equal(0, result.ConflictCount);
        // the padded content survives, re-quoted so it parses back identically
        Assert.Contains("1,\"  padded  \",10", text);
        Assert.Contains("2,plain,25", text);
    }

    [Fact]
    public void EditInsideQuotedWhitespaceIsARealChange()
    {
        const string baseCsv =
            "id,note,qty\n" +
            "1,\" x \",10\n" +
            "2,b,20\n" +
            "3,c,30\n";
        var ours = baseCsv.Replace("\" x \"", "\"  x \"");   // upstream widens padding
        var theirs = baseCsv.Replace("\" x \"", "\" x  \""); // rebased widens the other end
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        // ours kept in note; theirs' variant alongside in note_conflict, quoted to round-trip
        Assert.Contains("id,note,note_conflict,qty", text);
        Assert.Contains("1,\"  x \",\" x  \",10", text);
    }

    // -------------------------------------------- column reorganizations --

    [Fact]
    public void AllColumnsRenamedMergesWithDataEdits()
    {
        var ours = Base.Replace("id,name,qty", "ID,NAME,QTY");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "ID,NAME,QTY\n" +
            "1,apple,10\n" +
            "2,banana,25\n" +
            "3,cherry,30\n", text);
    }

    [Fact]
    public void AllColumnsRenamedAndShuffledMergesWithDataEdits()
    {
        const string ours =
            "QTY,ID,NAME\n" +
            "10,1,apple\n" +
            "20,2,banana\n" +
            "30,3,cherry\n";
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "QTY,ID,NAME\n" +
            "10,1,apple\n" +
            "25,2,banana\n" +
            "30,3,cherry\n", text);
    }

    [Fact]
    public void ColumnShuffleByRebasedSideIsDiscarded()
    {
        // theirs only reorders; the merged file replaces ours, so ours' layout wins
        const string theirs =
            "qty,id,name\n" +
            "10,1,apple\n" +
            "20,2,banana\n" +
            "30,3,cherry\n";
        var ours = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(Base.Replace("2,banana,20", "2,banana,25"), text);
    }

    // ----------------------------------------------- row reorganizations --

    [Fact]
    public void RowsShuffledInTheirsWithEditsInOurs()
    {
        const string theirs =           // full reversal, no edits
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "2,banana,20\n" +
            "1,apple,10\n";
        var ours = Base
            .Replace("2,banana,20", "2,banana,21")
            .Replace("3,cherry,30", "3,cherry,33");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        // theirs' ordering is respected; ours' edits follow the rows
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "2,banana,21\n" +
            "1,apple,10\n", text);
    }

    [Fact]
    public void RowsShuffledInOursWithEditsInTheirs()
    {
        const string ours =             // upstream reorganized, no cell edits
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "2,banana,20\n" +
            "1,apple,10\n";
        var theirs = Base
            .Replace("2,banana,20", "2,banana,22")
            .Replace("3,cherry,30", "3,cherry,33");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "2,banana,22\n" +
            "1,apple,10\n", text);
    }

    [Fact]
    public void MovedRowEditedByOtherSideMergesAtNewPosition()
    {
        const string theirs =           // cherry moved to the top, untouched
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30", "3,cherry,33");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }

    [Fact]
    public void MovedRowDeletedByOtherSideStaysDeleted()
    {
        const string theirs =           // cherry moved to the top, untouched
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30\n", ""); // upstream deleted cherry
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }

    [Fact]
    public void BothSidesMoveSameRowIdenticallyMergesOnce()
    {
        const string moved =
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var (text, result) = Merge(Base, moved, moved);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(moved, text);
    }

    [Fact]
    public void RowShuffleCombinedWithColumnRenameMergesCleanly()
    {
        // ours renames every column AND edits; theirs shuffles every row
        var ours = Base
            .Replace("id,name,qty", "key,title,count")
            .Replace("1,apple,10", "1,apple,11");
        const string theirs =
            "id,name,qty\n" +
            "2,banana,20\n" +
            "3,cherry,30\n" +
            "1,apple,10\n";
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "key,title,count\n" +
            "2,banana,20\n" +
            "3,cherry,30\n" +
            "1,apple,11\n", text);
    }
}
