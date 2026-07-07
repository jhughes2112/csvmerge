using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace CsvMerge;

/// <summary>
/// An in-memory CSV file: one header row plus data rows, all padded to a uniform width.
/// </summary>
public sealed class CsvTable
{
    public string[] Header { get; }
    public List<string[]> Rows { get; }

    private CsvTable(string[] header, List<string[]> rows)
    {
        Header = header;
        Rows = rows;
    }

    public static CsvTable Load(string path, string delimiter)
        => Parse(File.ReadAllText(path), delimiter);

    public static CsvTable Parse(string text, string delimiter)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            BadDataFound = null,
            DetectColumnCountChanges = false,
            // Whitespace outside quotes is not data: "1, apple" and "1,apple"
            // are the same content. Whitespace inside quotes is preserved.
            TrimOptions = TrimOptions.Trim,
        };

        string[]? header = null;
        var rows = new List<string[]>();
        using (var parser = new CsvParser(new StringReader(text), config))
        {
            while (parser.Read())
            {
                var record = parser.Record ?? [];
                if (header == null)
                    header = record;
                else
                    rows.Add(record);
            }
        }
        header ??= [];

        // Pad everything to a uniform width so column indexing is always safe.
        int width = header.Length;
        foreach (var row in rows)
            width = Math.Max(width, row.Length);

        return new CsvTable(Pad(header, width), rows.Select(r => Pad(r, width)).ToList());
    }

    private static string[] Pad(string[] cells, int width)
    {
        if (cells.Length == width) return cells;
        var padded = new string[width];
        Array.Copy(cells, padded, cells.Length);
        for (int i = cells.Length; i < width; i++) padded[i] = "";
        return padded;
    }
}
