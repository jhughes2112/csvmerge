using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

public class MergeTests
{
    // Rebase semantics: "ours" is the upstream version already committed (preserved
    // on conflict), "theirs" is the commit being replayed (each displaced value
    // lands in a per-column companion like "qty_conflict", ready to copy/paste).
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
    public void IdenticalInputsMergeCleanly()
    {
        var (text, result) = Merge(Base, Base, Base);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(Base, text);
    }

    [Fact]
    public void NonOverlappingCellEditsOnSameRowMerge()
    {
        var ours = Base.Replace("2,banana,20", "2,plantain,20");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("2,plantain,25", text);
        Assert.DoesNotContain("_conflict", text); // clean merges get no extra column
    }

    [Fact]
    public void CompetingEditsKeepOursAndAnnotateTheirs()
    {
        var ours = Base.Replace("2,banana,20", "2,banana,21");
        var theirs = Base.Replace("2,banana,20", "2,banana,22");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,qty_conflict\n" +
            "1,apple,10,\n" +
            "2,banana,21,22\n" +   // upstream value kept; displaced value alongside
            "3,cherry,30,\n", text);
    }

    [Fact]
    public void MultipleCompetingCellsEachGetTheirOwnCompanionColumn()
    {
        var ours = Base.Replace("2,banana,20", "2,plantain,21");
        var theirs = Base.Replace("2,banana,20", "2,burro,22");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,name_conflict,qty,qty_conflict\n" +
            "1,apple,,10,\n" +
            "2,plantain,burro,21,22\n" +
            "3,cherry,,30,\n", text);
    }

    [Fact]
    public void IdenticalEditsOnBothSidesMergeCleanly()
    {
        var edited = Base.Replace("2,banana,20", "2,banana,99");
        var (text, result) = Merge(Base, edited, edited);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("2,banana,99", text);
    }

    [Fact]
    public void UnrelatedRowAdditionsOnBothSidesAreKept()
    {
        var ours = Base + "4,dragonfruit,40\n";
        var theirs = Base + "5,elderberry,50\n";
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("4,dragonfruit,40", text);
        Assert.Contains("5,elderberry,50", text);
    }

    [Fact]
    public void IdenticalRowAdditionsAreDeduplicated()
    {
        var added = Base + "4,dragonfruit,40\n";
        var (text, result) = Merge(Base, added, added);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(added, text);
    }

    [Fact]
    public void SimilarRowAdditionsKeepOursAndAnnotateDifferences()
    {
        var ours = Base + "4,dragonfruit,40\n";
        var theirs = Base + "4,dragonfruit,44\n";
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("qty,qty_conflict", text);
        Assert.Contains("4,dragonfruit,40,44", text);
    }

    [Fact]
    public void DeleteVersusUntouchedDeletes()
    {
        var ours = Base.Replace("2,banana,20\n", "");
        var (text, result) = Merge(Base, ours, Base);
        Assert.Equal(0, result.ConflictCount);
        Assert.DoesNotContain("banana", text);
        Assert.Contains("3,cherry,30", text);
    }

    [Fact]
    public void DeleteInOursVersusEditInTheirsKeepsEditedRowAnnotated()
    {
        var ours = Base.Replace("2,banana,20\n", "");
        var theirs = Base.Replace("2,banana,20", "2,banana,99");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("2,banana,99,deleted in upstream", text);
    }

    [Fact]
    public void EditInOursVersusDeleteInTheirsKeepsEditedRowAnnotated()
    {
        var ours = Base.Replace("2,banana,20", "2,banana,99");
        var theirs = Base.Replace("2,banana,20\n", "");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("2,banana,99,deleted in rebased", text);
    }

    [Fact]
    public void ColumnRenameMergesWithDataEditsFromOtherSide()
    {
        var ours = Base.Replace("id,name,qty", "id,name,quantity");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.StartsWith("id,name,quantity\n", text);
        Assert.Contains("2,banana,25", text);
    }

    [Fact]
    public void CompetingColumnRenamesKeepOursNameAndShowTheirsInCompanion()
    {
        var ours = Base.Replace("id,name,qty", "id,name,quantity");
        var theirs = Base.Replace("id,name,qty", "id,name,count");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        // ours' name is kept; the companion is named after theirs' proposal
        Assert.Equal(
            "id,name,quantity,count_conflict\n" +
            "1,apple,10,\n" +
            "2,banana,20,\n" +
            "3,cherry,30,\n", text);
    }

    [Fact]
    public void ColumnAddedOnOneSideRowAddedOnOther()
    {
        var ours =
            "id,name,qty,color\n" +
            "1,apple,10,red\n" +
            "2,banana,20,yellow\n" +
            "3,cherry,30,red\n";
        var theirs = Base + "4,dragonfruit,40\n";
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.StartsWith("id,name,qty,color\n", text);
        Assert.Contains("2,banana,20,yellow", text);
        Assert.Contains("4,dragonfruit,40,", text); // new row gets an empty cell in the new column
    }

    [Fact]
    public void ColumnDeletedVersusUntouchedDeletes()
    {
        var ours =
            "id,name\n" +
            "1,apple\n" +
            "2,banana\n" +
            "3,cherry\n";
        var (text, result) = Merge(Base, ours, Base);
        Assert.Equal(0, result.ConflictCount);
        Assert.StartsWith("id,name\n", text);
        Assert.DoesNotContain("10", text);
    }

    [Fact]
    public void ColumnDeletedVersusEditedConflictsAndKeepsColumn()
    {
        var ours =
            "id,name\n" +
            "1,apple\n" +
            "2,banana\n" +
            "3,cherry\n";
        var theirs = Base.Replace("2,banana,20", "2,banana,99");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("qty", text);
        Assert.Contains("2,banana,99", text);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void ColumnReorderInOursIsRespectedAndDataMerges()
    {
        var ours =
            "name,id,qty\n" +
            "apple,1,10\n" +
            "banana,2,20\n" +
            "cherry,3,30\n";
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.StartsWith("name,id,qty\n", text); // result follows ours' column order
        Assert.Contains("banana,2,25", text);
    }

    [Fact]
    public void QuotedFieldsWithDelimitersAndNewlinesSurvive()
    {
        const string tricky =
            "id,note\n" +
            "1,\"hello, world\"\n" +
            "2,\"line one\nline two\"\n";
        var ours = tricky.Replace("hello, world", "hello, there");
        var (text, result) = Merge(tricky, ours, tricky);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("\"hello, there\"", text);
        Assert.Contains("\"line one\nline two\"", text);
    }

    [Fact]
    public void ConflictValueContainingDelimiterIsQuoted()
    {
        var ours = Base.Replace("2,banana,20", "2,banana,21");
        var theirs = Base.Replace("2,banana,20", "2,\"ba,na; x=y\",22");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        // theirs' name edit merges cleanly (ours didn't touch it); only qty conflicts
        Assert.Contains("2,\"ba,na; x=y\",21,22", text);
    }

    [Fact]
    public void RowInsertedInMiddleKeepsPosition()
    {
        var ours = Base.Replace("2,banana,20\n", "2,banana,20\n25,blueberry,25\n");
        var (text, result) = Merge(Base, ours, Base);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "25,blueberry,25\n" +
            "3,cherry,30\n", text);
    }

    [Fact]
    public void BothSidesAddSameColumnWithSameValuesMergesToOne()
    {
        var withColor =
            "id,name,qty,color\n" +
            "1,apple,10,red\n" +
            "2,banana,20,yellow\n" +
            "3,cherry,30,red\n";
        var (text, result) = Merge(Base, withColor, withColor);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(withColor, text);
    }

    [Fact]
    public void CompanionColumnNameAvoidsCollisions()
    {
        // the data already has a column literally named "qty_conflict"
        const string baseCsv =
            "id,qty,qty_conflict\n" +
            "1,10,x\n" +
            "2,20,y\n";
        var ours = baseCsv.Replace("1,10,x", "1,11,x");
        var theirs = baseCsv.Replace("1,10,x", "1,12,x");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,qty,_qty_conflict,qty_conflict\n" +
            "1,11,12,x\n" +
            "2,20,,y\n", text);
    }

    [Fact]
    public void RowStatusColumnNameAvoidsCollisions()
    {
        const string baseCsv =
            "id,name,_conflict\n" +
            "1,a,x\n" +
            "2,b,y\n";
        var ours = baseCsv.Replace("2,b,y\n", "");            // upstream deletes row 2
        var theirs = baseCsv.Replace("2,b,y", "2,bb,y");      // rebased edits it
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(
            "id,name,_conflict,__conflict\n" +
            "1,a,x,\n" +
            "2,bb,y,deleted in upstream\n", text);
    }
}
