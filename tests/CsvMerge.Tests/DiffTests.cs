using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

/// <summary>
/// The semantic diff: cell-level change reporting on the same alignment engine
/// as the merge. Rows are labelled by the auto-detected discriminating column.
/// </summary>
public class DiffTests
{
    private static (string Text, bool Different) Diff(string oldCsv, string newCsv)
        => SemanticDiff.Diff(CsvTable.Parse(oldCsv, ","), CsvTable.Parse(newCsv, ","), ",");

    private const string Base =
        "id,name,qty\n" +
        "1,apple,10\n" +
        "2,banana,20\n" +
        "3,cherry,30\n";

    [Fact]
    public void IdenticalFilesProduceNoDiff()
    {
        var (text, different) = Diff(Base, Base);
        Assert.False(different);
        Assert.Equal("", text);
    }

    [Fact]
    public void QuoteAndWhitespaceOnlyChangesAreInvisible()
    {
        const string requoted =
            "\"id\", \"name\", \"qty\"\n" +
            "\"1\",   \"apple\",  \"10\"\n" +
            "\"2\",   \"banana\", \"20\"\n" +
            "\"3\",   \"cherry\", \"30\"\n";
        var (text, different) = Diff(Base, requoted);
        Assert.False(different);
        Assert.Equal("", text);
    }

    [Fact]
    public void CellChangeIsReportedWithAutoDetectedLabel()
    {
        var (text, different) = Diff(Base, Base.Replace("2,banana,20", "2,banana,25"));
        Assert.True(different);
        Assert.Equal("~ row [id=2]: qty: 20 -> 25\n", text);
    }

    [Fact]
    public void MultipleCellChangesAreJoined()
    {
        var (text, _) = Diff(Base, Base.Replace("2,banana,20", "2,plantain,25"));
        Assert.Equal("~ row [id=2]: name: banana -> plantain; qty: 20 -> 25\n", text);
    }

    [Fact]
    public void RowAddedAndRemovedShowContent()
    {
        var added = Base + "4,dragonfruit,40\n";
        var (text, _) = Diff(Base, added);
        Assert.Equal("+ row [id=4]: 4,dragonfruit,40\n", text);

        var removed = Base.Replace("3,cherry,30\n", "");
        (text, _) = Diff(Base, removed);
        Assert.Equal("- row [id=3]: 3,cherry,30\n", text);
    }

    [Fact]
    public void RowMoveIsReportedWithPositions()
    {
        const string moved =
            "id,name,qty\n" +
            "3,cherry,30\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var (text, _) = Diff(Base, moved);
        Assert.Equal("> row [id=3] moved (3 -> 1)\n", text);
    }

    [Fact]
    public void MovedAndEditedRowShowsBoth()
    {
        const string movedEdited =
            "id,name,qty\n" +
            "3,cherry,33\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        var (text, _) = Diff(Base, movedEdited);
        Assert.Equal("> row [id=3] moved (3 -> 1): qty: 30 -> 33\n", text);
    }

    [Fact]
    public void ColumnRenameAddAndRemoveAreReported()
    {
        var renamed = Base.Replace("id,name,qty", "id,name,count");
        var (text, _) = Diff(Base, renamed);
        Assert.Equal("~ col qty -> count\n", text);

        const string addedCol =
            "id,name,qty,stock\n" +
            "1,apple,10,yes\n" +
            "2,banana,20,no\n" +
            "3,cherry,30,yes\n";
        (text, _) = Diff(Base, addedCol);
        Assert.Equal("+ col stock\n", text);

        const string removedCol =
            "id,name\n" +
            "1,apple\n" +
            "2,banana\n" +
            "3,cherry\n";
        (text, _) = Diff(Base, removedCol);
        Assert.Equal("- col qty\n", text);
    }

    [Fact]
    public void ColumnReorderIsReportedAndRowsStayQuiet()
    {
        const string reordered =
            "qty,id,name\n" +
            "10,1,apple\n" +
            "20,2,banana\n" +
            "30,3,cherry\n";
        var (text, _) = Diff(Base, reordered);
        Assert.Equal("> cols reordered: id,name,qty -> qty,id,name\n", text);
    }

    [Fact]
    public void LabelFallsBackToRowNumberWithoutADistinctiveColumn()
    {
        const string oldCsv =
            "status,tag\n" +
            "active,x\n" +
            "active,x\n" +
            "active,y\n";
        var newCsv = oldCsv.Replace("active,y", "retired,y");
        var (text, _) = Diff(oldCsv, newCsv);
        Assert.Equal("~ row [#3]: status: active -> retired\n", text);
    }

    [Fact]
    public void LabelUsesTheMostDistinctiveColumnNotTheFirst()
    {
        const string oldCsv =
            "status,id\n" +
            "active,1\n" +
            "active,2\n" +
            "active,3\n";
        var newCsv = oldCsv.Replace("active,2", "retired,2");
        var (text, _) = Diff(oldCsv, newCsv);
        Assert.Equal("~ row [id=2]: status: active -> retired\n", text);
    }

    [Fact]
    public void ValuesWithDelimitersAreQuotedInTheReport()
    {
        var newCsv = Base.Replace("2,banana,20", "2,\"ba,nana\",20");
        var (text, _) = Diff(Base, newCsv);
        Assert.Equal("~ row [id=2]: name: banana -> \"ba,nana\"\n", text);
    }

    [Fact]
    public void CreatedFileShowsEverythingAsAdded()
    {
        var (text, different) = Diff("", Base);
        Assert.True(different);
        Assert.Contains("+ col id\n", text);
        Assert.Contains("+ col name\n", text);
        Assert.Contains("+ col qty\n", text);
        Assert.Contains("+ row", text);
        Assert.Contains("1,apple,10\n", text);
    }

    [Fact]
    public void BigReorganizationStaysReadable()
    {
        // rename a column, reorder rows, edit a cell, add a row — a change a
        // line diff would render as a total rewrite.
        const string newCsv =
            "id,name,count\n" +
            "3,cherry,31\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "4,dragonfruit,40\n";
        var (text, _) = Diff(Base, newCsv);
        Assert.Equal(
            "~ col qty -> count\n" +
            "> row [id=3] moved (3 -> 1): count: 30 -> 31\n" +
            "+ row [id=4]: 4,dragonfruit,40\n", text);
    }
}
