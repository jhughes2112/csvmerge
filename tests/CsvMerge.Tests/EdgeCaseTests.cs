using System.Diagnostics;
using System.Text;
using CsvMerge;
using Xunit;

namespace CsvMerge.Tests;

/// <summary>
/// Structural and content extremes: degenerate file shapes, exotic cell
/// content, delimiter variants, and a performance smoke test.
/// </summary>
public class EdgeCaseTests
{
    private static (string Text, MergeResult Result) Merge(string baseCsv, string oursCsv, string theirsCsv,
        string delimiter = ",")
    {
        var result = ThreeWayMerger.Merge(
            CsvTable.Parse(baseCsv, delimiter),
            CsvTable.Parse(oursCsv, delimiter),
            CsvTable.Parse(theirsCsv, delimiter),
            "upstream", "rebased");
        var text = MergeWriter.Write(result, delimiter, "\n");
        return (text, result);
    }

    private const string Base =
        "id,name,qty\n" +
        "1,apple,10\n" +
        "2,banana,20\n" +
        "3,cherry,30\n";

    // ------------------------------------------------- degenerate shapes --

    [Fact]
    public void EmptyBaseBothSidesAddContent()
    {
        // add/add: the file didn't exist in the ancestor
        const string ours =
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n";
        const string theirs =
            "id,name,qty\n" +
            "1,apple,10\n" +
            "3,cherry,30\n";
        var (text, result) = Merge("", ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "1,apple,10\n" +      // identical addition deduplicated
            "2,banana,20\n" +
            "3,cherry,30\n", text);
    }

    [Fact]
    public void HeaderOnlyBaseBothSidesAddRows()
    {
        const string baseCsv = "id,name,qty\n";
        var ours = baseCsv + "1,apple,10\n";
        var theirs = baseCsv + "2,banana,20\n";
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("id,name,qty\n1,apple,10\n2,banana,20\n", text);
    }

    [Fact]
    public void SingleColumnFileConflicts()
    {
        const string baseCsv = "val\na\nb\nc\n";
        var ours = baseCsv.Replace("b", "B1");
        var theirs = baseCsv.Replace("b", "B2");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal("val,val_conflict\na,\nB1,B2\nc,\n", text);
    }

    [Fact]
    public void DuplicateRowsInBaseEditOneOfThem()
    {
        const string baseCsv =
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "2,banana,20\n" +
            "3,cherry,30\n";
        var ours =
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "2,banana,21\n" +   // only the second copy edited
            "3,cherry,30\n";
        var theirs = baseCsv.Replace("3,cherry,30", "3,cherry,33");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty\n" +
            "1,apple,10\n" +
            "2,banana,20\n" +
            "2,banana,21\n" +
            "3,cherry,33\n", text);
    }

    [Fact]
    public void DuplicateColumnNamesMapByOccurrence()
    {
        const string baseCsv =
            "id,qty,qty\n" +
            "1,10,100\n" +
            "2,20,200\n" +
            "3,30,300\n";
        var ours = baseCsv.Replace("2,20,200", "2,20,201");   // second qty
        var theirs = baseCsv.Replace("2,20,200", "2,21,200"); // first qty
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("2,21,201", text);
    }

    [Fact]
    public void ColumnAddedAtFrontByTheirs()
    {
        const string theirs =
            "seq,id,name,qty\n" +
            "s1,1,apple,10\n" +
            "s2,2,banana,20\n" +
            "s3,3,cherry,30\n";
        var ours = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "seq,id,name,qty\n" +
            "s1,1,apple,10\n" +
            "s2,2,banana,25\n" +
            "s3,3,cherry,30\n", text);
    }

    [Fact]
    public void BothSidesAddDifferentColumnsOursFirst()
    {
        var ours = Base
            .Replace("id,name,qty", "id,name,qty,stock")
            .Replace("1,apple,10", "1,apple,10,yes")
            .Replace("2,banana,20", "2,banana,20,no")
            .Replace("3,cherry,30", "3,cherry,30,yes");
        var theirs = Base
            .Replace("id,name,qty", "id,name,qty,rating")
            .Replace("1,apple,10", "1,apple,10,5")
            .Replace("2,banana,20", "2,banana,20,4")
            .Replace("3,cherry,30", "3,cherry,30,3");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,stock,rating\n" +
            "1,apple,10,yes,5\n" +
            "2,banana,20,no,4\n" +
            "3,cherry,30,yes,3\n", text);
    }

    [Fact]
    public void RaggedRowsArePaddedToUniformWidth()
    {
        const string baseCsv =
            "id,name,qty\n" +
            "1,apple\n" +                // short row
            "2,banana,20,extra\n" +      // long row
            "3,c,3\n";
        var theirs = baseCsv.Replace("1,apple\n", "1,apricot\n");
        var (text, result) = Merge(baseCsv, baseCsv, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(
            "id,name,qty,\n" +
            "1,apricot,,\n" +
            "2,banana,20,extra\n" +
            "3,c,3,\n", text);
    }

    // --------------------------------------------------- content extremes --

    [Fact]
    public void CaseOnlyHeaderRenameIsARename()
    {
        var ours = Base.Replace("id,name,qty", "id,name,Qty");
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.StartsWith("id,name,Qty\n", text);
        Assert.Contains("2,banana,25", text);
    }

    [Fact]
    public void UnicodeContentMergesAndAnnotates()
    {
        const string baseCsv =
            "id,fruit\n" +
            "1,\U0001F34E\n" +
            "2,café\n" +
            "3,林檎\n";
        var ours = baseCsv.Replace("\U0001F34E", "\U0001F34F");
        var theirs = baseCsv.Replace("\U0001F34E", "\U0001F350");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("id,fruit,fruit_conflict", text);
        Assert.Contains("1,\U0001F34F,\U0001F350", text);
        Assert.Contains("2,café,", text);
        Assert.Contains("3,林檎,", text);
    }

    [Fact]
    public void EmbeddedQuotesRoundTripAndAnnotate()
    {
        const string baseCsv =
            "id,note\n" +
            "1,\"he said \"\"hi\"\"\"\n" +
            "2,b\n" +
            "3,c\n";
        var ours = baseCsv.Replace("he said \"\"hi\"\"", "say \"\"yo\"\"");
        var theirs = baseCsv.Replace("he said \"\"hi\"\"", "say \"\"sup\"\"");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("1,\"say \"\"yo\"\"\",\"say \"\"sup\"\"\"", text);
    }

    [Fact]
    public void NewlineInsideConflictingCellQuotesTheAnnotation()
    {
        const string baseCsv =
            "id,note\n" +
            "1,orig\n" +
            "2,b\n" +
            "3,c\n";
        var ours = baseCsv.Replace("1,orig", "1,single");
        var theirs = baseCsv.Replace("1,orig", "1,\"line1\nline2\"");
        var (text, result) = Merge(baseCsv, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("1,single,\"line1\nline2\"", text);
        // the annotated output must still be parseable CSV
        var reparsed = CsvTable.Parse(text, ",");
        Assert.Equal(3, reparsed.Rows.Count);
    }

    [Fact]
    public void DataThatLooksLikeAnnotationsIsJustData()
    {
        const string baseCsv =
            "id,memo\n" +
            "1,qty=99\n" +
            "2,deleted in upstream\n" +
            "3,c\n";
        var theirs = baseCsv.Replace("3,c", "3,d");
        var (text, result) = Merge(baseCsv, baseCsv, theirs);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("1,qty=99\n", text);
        Assert.Contains("2,deleted in upstream\n", text);
        Assert.Contains("3,d\n", text);
    }

    [Fact]
    public void NumericFormattingIsStringSemantics()
    {
        // "10.0" and "010" may be the same number, but cells are strings
        var ours = Base.Replace("1,apple,10", "1,apple,10.0");
        var theirs = Base.Replace("1,apple,10", "1,apple,010");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("1,apple,10.0,010", text);
    }

    [Fact]
    public void SemicolonDelimiterWorksAndCommasAreNotSpecial()
    {
        const string baseCsv = "id;note\n1;a,b\n2;c\n3;d\n";
        var theirs = baseCsv.Replace("2;c", "2;C");
        var (text, result) = Merge(baseCsv, baseCsv, theirs, ";");
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("id;note\n1;a,b\n2;C\n3;d\n", text);
    }

    // ---------------------------------------------------- brutal combos --

    [Fact]
    public void AllRowsDeletedInOursVersusEditInTheirs()
    {
        const string headerOnly = "id,name,qty\n";
        var theirs = Base.Replace("2,banana,20", "2,banana,25");
        var (text, result) = Merge(Base, headerOnly, theirs);
        Assert.Equal(1, result.ConflictCount);
        // only the edited row survives, flagged; the untouched rows stay deleted
        Assert.Equal(
            "id,name,qty,_conflict\n" +
            "2,banana,25,deleted in upstream\n", text);
    }

    [Fact]
    public void RowReplacedVersusEditProducesHybridWithConflict()
    {
        // ours edits one cell of cherry; theirs replaces the whole line.
        // diff3 chunk semantics pair the replacement with the original row, so
        // theirs' uncontested cells win and the contested one is annotated.
        var ours = Base.Replace("3,cherry,30", "3,cherry,33");
        var theirs = Base.Replace("3,cherry,30", "9,kumquat,90");
        var (text, result) = Merge(Base, ours, theirs);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains("9,kumquat,33,90", text);
    }

    [Fact]
    public void LargeFileMergesQuicklyWithoutConflicts()
    {
        const int rows = 20_000;
        var sb = new StringBuilder("id,name,qty,tag\n");
        for (int i = 0; i < rows; i++)
            sb.Append(i).Append(",name").Append(i).Append(',').Append(i * 2).Append(",x").Append(i % 7).Append('\n');
        var baseCsv = sb.ToString();

        var oursSb = new StringBuilder("id,name,qty,tag\n");
        var theirsSb = new StringBuilder("id,name,qty,tag\n");
        for (int i = 0; i < rows; i++)
        {
            oursSb.Append(i).Append(",name").Append(i).Append(',')
                .Append(i % 97 == 0 ? i * 2 + 1 : i * 2).Append(",x").Append(i % 7).Append('\n');
            theirsSb.Append(i).Append(',')
                .Append(i % 89 == 0 ? $"renamed{i}" : $"name{i}").Append(',')
                .Append(i * 2).Append(",x").Append(i % 7).Append('\n');
        }

        var watch = Stopwatch.StartNew();
        var (text, result) = Merge(baseCsv, oursSb.ToString(), theirsSb.ToString());
        watch.Stop();

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains("0,renamed0,1,x0", text);      // i=0: edited by BOTH sides (different cells), merged
        Assert.Contains("97,name97,195,x6", text);     // ours-only edit
        Assert.Contains("89,renamed89,178,x5", text);  // theirs-only edit
        Assert.True(watch.ElapsedMilliseconds < 15_000, $"merge took {watch.ElapsedMilliseconds} ms");
    }
}
