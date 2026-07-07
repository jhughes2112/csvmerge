using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

/// <summary>
/// Distinctiveness-weighted row identity: agreement on a unique column (an id)
/// is strong evidence two rows are the same row; agreement on a column that
/// says the same thing everywhere is almost none. These tests use tables where
/// unweighted cell-counting gives the WRONG answer either way — pairing
/// unrelated rows that share boilerplate, or missing a real move because the
/// edits happened to touch several low-value columns.
/// </summary>
public class WeightedSimilarityTests
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

    // id/name/qty are distinctive (weight 1.0); status/region/flag are constant
    // (weight 0.2 each with five rows), so boilerplate agreement is worth little.
    private const string Base =
        "id,name,qty,status,region,flag\n" +
        "1,apple,10,active,us,x\n" +
        "2,banana,20,active,us,x\n" +
        "3,cherry,30,active,us,x\n" +
        "4,durian,40,active,us,x\n" +
        "5,elderberry,50,active,us,x\n";

    [Fact]
    public void BoilerplateAgreementDoesNotFakeAMove()
    {
        // rebased deletes cherry and adds an unrelated kumquat that shares only
        // the three constant columns (3 of 6 cells = 0.5 unweighted, which the
        // old counting would have fused into a bogus "moved and edited" row).
        var theirs = Base
            .Replace("3,cherry,30,active,us,x\n", "")
            + "9,kumquat,90,active,us,x\n";
        var ours = Base.Replace("3,cherry,30", "3,cherries,30"); // upstream edits cherry
        var (text, result) = Merge(Base, ours, theirs);

        // honest outcome: a delete/edit conflict on cherry, kumquat as its own row
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,status,region,flag,_conflict\n" +
            "1,apple,10,active,us,x,\n" +
            "2,banana,20,active,us,x,\n" +
            "3,cherries,30,active,us,x,deleted in rebased\n" +
            "4,durian,40,active,us,x,\n" +
            "5,elderberry,50,active,us,x,\n" +
            "9,kumquat,90,active,us,x,\n", text);
    }

    [Fact]
    public void MoveIsDetectedEvenWhenEditsSwampTheCellCount()
    {
        // rebased moves cherry to the top and edits qty, status AND region:
        // only id and name still agree (2 of 6 cells = 0.33 unweighted — the old
        // counting missed this move), but they are the two columns that matter.
        var theirs =
            "id,name,qty,status,region,flag\n" +
            "3,cherry,31,inactive,eu,x\n" +
            "1,apple,10,active,us,x\n" +
            "2,banana,20,active,us,x\n" +
            "4,durian,40,active,us,x\n" +
            "5,elderberry,50,active,us,x\n";
        var ours = Base.Replace("3,cherry,30", "3,cherries,30"); // upstream renames it
        var (text, result) = Merge(Base, ours, theirs);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,status,region,flag\n" +
            "3,cherries,31,inactive,eu,x\n" +   // move + both sides' edits, at the new position
            "1,apple,10,active,us,x\n" +
            "2,banana,20,active,us,x\n" +
            "4,durian,40,active,us,x\n" +
            "5,elderberry,50,active,us,x\n", text);
    }

    [Fact]
    public void BothSidesAddingBoilerplateRowsAreNotFusedIntoOne()
    {
        // fig and kumquat share only the constant columns; unweighted counting
        // (3 of 6 = 0.5) would have merged them into one row with conflicts.
        var ours = Base + "8,fig,80,active,us,x\n";
        var theirs = Base + "9,kumquat,90,active,us,x\n";
        var (text, result) = Merge(Base, ours, theirs);

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("8,fig,80,active,us,x\n", text);
        Assert.Contains("9,kumquat,90,active,us,x\n", text);
    }

    [Fact]
    public void GenuinelySimilarAdditionsStillPairDespiteWeights()
    {
        // same id, name, flag (distinctive cells agree) -> still recognized as
        // the same logical record; the differing qty is annotated.
        var ours = Base + "8,fig,80,active,us,x\n";
        var theirs = Base + "8,fig,88,active,us,x\n";
        var (text, result) = Merge(Base, ours, theirs);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("qty,qty_conflict", text);
        Assert.Contains("8,fig,80,88,active,us,x", text);
    }

    [Fact]
    public void AllUniqueTableBehavesExactlyAsBefore()
    {
        // when every column is distinctive the weights are uniform and nothing
        // about the established semantics changes.
        const string baseCsv =
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "3,cherry,30\n";
        var ours = baseCsv.Replace("2,banana,20", "2,banana,21");
        var theirs = baseCsv.Replace("2,banana,20", "2,banana,22");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("2,banana,21,22", text);
    }
}
