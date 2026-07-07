using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

/// <summary>
/// Rows that were moved AND edited in the same commit. Similarity-based move
/// detection pairs a deleted base row with an inserted row when their content
/// still mostly agrees, so the other side's changes fold in at the row's new
/// position instead of degrading to a delete/edit conflict plus a stale copy.
/// </summary>
public class MoveEditTests
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

    [Fact]
    public void MovedAndEditedVersusUntouchedMergesAtNewPosition()
    {
        const string theirs =           // cherry moved to the top AND qty edited
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var (text, result) = Merge(Base, Base, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }

    [Fact]
    public void MovedAndEditedVersusEditInDifferentColumnMergesBoth()
    {
        const string theirs =           // cherry moved to the top AND qty edited
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30", "3,cherries,30"); // upstream renames it
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherries,33\n" +         // upstream's name + rebased's qty, at the new position
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }

    [Fact]
    public void MovedAndEditedVersusEditInSameCellConflictsAtNewPosition()
    {
        const string theirs =           // cherry moved to the top AND qty -> 33
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30", "3,cherry,35");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,qty_conflict\n" +
            "3,cherry,35,33\n" +
            "1,apple,10,\n" +
            "2,banana,20,\n", text);
    }

    [Fact]
    public void MovedAndEditedVersusDeleteIsADeleteEditConflictAtNewPosition()
    {
        const string theirs =           // cherry moved to the top AND qty -> 33
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30\n", ""); // upstream deleted cherry
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,_conflict\n" +
            "3,cherry,33,deleted in upstream\n" +
            "1,apple,10,\n" +
            "2,banana,20,\n", text);
    }

    [Fact]
    public void MovedWithoutEditsVersusDeleteStillLosesToTheDelete()
    {
        const string theirs =           // cherry moved to the top, untouched
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var ours = Base.Replace("3,cherry,30\n", "");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }

    [Fact]
    public void BothSidesMoveAndEditSameRowDifferently()
    {
        const string ours =             // upstream: cherry to the top, qty -> 35
            "id,name,qty\n" +
            "3,cherry,35\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        const string theirs =           // rebased: cherry to the middle, qty -> 33
            "id,name,qty\n" +
            "1,apple,10\n" +
            "3,cherry,33\n" +
            "2,banana,20\n";
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        // upstream's position and value win; rebased's value alongside
        Assert.Equal(
            "id,name,qty,qty_conflict\n" +
            "3,cherry,35,33\n" +
            "1,apple,10,\n" +
            "2,banana,20,\n", text);
    }

    [Fact]
    public void EveryRowShuffledAndEditedVersusCellEdits()
    {
        const string theirs =           // full reversal AND every qty bumped
            "id,name,qty\n" +
            "3,cherry,31\n" +
            "2,banana,21\n" +
            "1,apple,11\n";
        var ours = Base.Replace("1,apple,10", "1,apricot,10"); // upstream renames apple
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "3,cherry,31\n" +
            "2,banana,21\n" +
            "1,apricot,11\n", text);
    }

    [Fact]
    public void DissimilarInsertIsNotMistakenForAMove()
    {
        const string theirs =           // cherry deleted; an unrelated row added elsewhere
            "id,name,qty\n" +
            "9,kumquat,90\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var (text, result) = Merge(Base, Base, theirs);
        Assert.Equal(0, result.ConflictCount);
        // kumquat shares no cells with cherry (similarity 0): a real add + a real delete
        Assert.Equal(
            "id,name,qty\n" +
            "9,kumquat,90\n" +
            "1,apple,10\n" +
            "2,banana,20\n", text);
    }
}
