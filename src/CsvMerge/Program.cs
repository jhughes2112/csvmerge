using System.Diagnostics;
using System.Text;

namespace CsvMerge;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"csvmerge: error: {ex.Message}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }
        if (args[0] == "install")
            return Install(args.Skip(1).ToArray());
        if (args[0] == "diff")
            return DiffCommand(args.Skip(1).ToArray());
        if (args[0] == "gitdiff")
            return GitDiffCommand(args.Skip(1).ToArray());

        var positional = new List<string>();
        string? output = null;
        string delimiter = ",";
        string labelOurs = "upstream", labelTheirs = "rebased";
        string conflictSuffix = "_conflict";
        bool toStdout = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output": output = Next(args, ref i); break;
                case "-d" or "--delimiter": delimiter = Next(args, ref i); break;
                case "--conflict-suffix": conflictSuffix = Next(args, ref i); break;
                case "--label-ours": labelOurs = Next(args, ref i); break;
                case "--label-theirs": labelTheirs = Next(args, ref i); break;
                case "--stdout": toStdout = true; break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"unknown option '{args[i]}' (see --help)");
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count != 3)
            throw new ArgumentException("expected exactly three files: <base> <ours> <theirs> (see --help)");

        string basePath = positional[0], oursPath = positional[1], theirsPath = positional[2];
        string oursText = File.ReadAllText(oursPath);

        var baseTable = CsvTable.Load(basePath, delimiter);
        var ours = CsvTable.Parse(oursText, delimiter);
        var theirs = CsvTable.Load(theirsPath, delimiter);

        var result = ThreeWayMerger.Merge(baseTable, ours, theirs, labelOurs, labelTheirs);

        string newline = oursText.Contains("\r\n") ? "\r\n" : "\n";
        string text = MergeWriter.Write(result, delimiter, newline, conflictSuffix);

        if (toStdout)
        {
            Console.Out.Write(text);
        }
        else
        {
            // Merge-driver contract: the result replaces "ours" (%A). Preserve its BOM if it had one.
            bool hadBom = HasUtf8Bom(oursPath);
            File.WriteAllText(output ?? oursPath, text, new UTF8Encoding(hadBom));
        }

        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"csvmerge: conflict: {warning}");

        if (result.ConflictCount > 0)
        {
            Console.Error.WriteLine($"csvmerge: {result.ConflictCount} conflict(s) in {oursPath}");
            return 1;
        }
        return 0;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"option '{args[i]}' requires a value");
        return args[++i];
    }

    /// <summary>csvmerge diff &lt;old&gt; &lt;new&gt; — semantic diff; exit 0 same, 1 different, 2 error.</summary>
    private static int DiffCommand(string[] args)
    {
        var positional = new List<string>();
        string delimiter = ",";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-d" or "--delimiter": delimiter = Next(args, ref i); break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"unknown option '{args[i]}' (see --help)");
                    positional.Add(args[i]);
                    break;
            }
        }
        if (positional.Count != 2)
            throw new ArgumentException("expected exactly two files: diff <old> <new>");

        var oldTable = LoadOrEmpty(positional[0], delimiter);
        var newTable = LoadOrEmpty(positional[1], delimiter);
        var (text, different) = SemanticDiff.Diff(oldTable, newTable, delimiter);
        if (different)
        {
            Console.WriteLine($"--- {positional[0]}");
            Console.WriteLine($"+++ {positional[1]}");
            Console.Out.Write(text);
        }
        return different ? 1 : 0;
    }

    /// <summary>
    /// Git external-diff contract (diff.csvmerge.command): called with
    /// path old-file old-hex old-mode new-file new-hex new-mode [new-path similarity].
    /// Output is informational; always exits 0.
    /// </summary>
    private static int GitDiffCommand(string[] args)
    {
        if (args.Length < 7) return 0; // unmerged entry or unknown shape: nothing to show

        string path = args[0];
        string newPath = args.Length >= 9 ? args[7] : path;
        var oldTable = LoadOrEmpty(args[1], ",");
        var newTable = LoadOrEmpty(args[4], ",");

        var (text, different) = SemanticDiff.Diff(oldTable, newTable, ",");
        if (different)
        {
            Console.WriteLine($"csvdiff a/{path} b/{newPath}");
            Console.Out.Write(text);
        }
        return 0;
    }

    /// <summary>Git hands /dev/null (or a missing path) for created/deleted files.</summary>
    private static CsvTable LoadOrEmpty(string path, string delimiter)
    {
        if (path == "/dev/null" || path == "nul" || !File.Exists(path))
            return CsvTable.Parse("", delimiter);
        return CsvTable.Load(path, delimiter);
    }

    private static bool HasUtf8Bom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        return stream.Read(bom) == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
    }

    private static int Install(string[] args)
    {
        bool global = args.Contains("--global");
        string scope = global ? "--global" : "--local";

        GitConfig(scope, "merge.csvmerge.name", "CSV three-way merge driver (csvmerge)");
        GitConfig(scope, "merge.csvmerge.driver", "csvmerge %O %A %B");
        GitConfig(scope, "diff.csvmerge.command", "csvmerge gitdiff");

        Console.WriteLine($"Registered the 'csvmerge' merge and diff drivers in {(global ? "global" : "local")} git config.");
        Console.WriteLine("Now map CSV files to them in .gitattributes (checked in) or .git/info/attributes:");
        Console.WriteLine();
        Console.WriteLine("    *.csv merge=csvmerge diff=csvmerge");
        Console.WriteLine();
        Console.WriteLine("csvmerge must be on PATH when git runs the drivers.");
        return 0;
    }

    private static void GitConfig(string scope, string key, string value)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("config");
        psi.ArgumentList.Add(scope);
        psi.ArgumentList.Add(key);
        psi.ArgumentList.Add(value);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git config exited with code {process.ExitCode}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            csvmerge - git-native three-way merge driver for CSV files

            Usage:
              csvmerge <base> <ours> <theirs> [options]
              csvmerge diff <old> <new> [-d <delimiter>]
              csvmerge install [--global]

            Merges <ours> and <theirs> using <base> as the common ancestor and writes
            the result over <ours> (the git merge-driver contract). Rows and columns
            are aligned structurally from the two diffs against the ancestor - no key
            column is required.

            Built for rebase: during a rebase, <ours> is the version already committed
            upstream and <theirs> is your commit being replayed. Competing changes keep
            the upstream value in the data cells; each conflicted column gets a
            companion column right after it (e.g. "qty_conflict") holding exactly the
            displaced value, ready to copy over the kept one. Delete-vs-edit rows are
            flagged in a trailing "_conflict" status column. The file stays valid CSV
            and the exit code is 1, which stops the rebase for review.

            Options:
              -o, --output <file>       write the result here instead of over <ours>
                  --stdout              print the result instead of writing a file
              -d, --delimiter <str>     field delimiter (default ",")
                  --conflict-suffix <s> suffix for conflict companion columns and the
                                        name of the row-status column (default "_conflict")
                  --label-ours <s>      label for the ours side (default "upstream")
                  --label-theirs <s>    label for the theirs side (default "rebased")

            Exit codes: 0 clean merge, 1 conflicts, 2 error.

            diff prints a cell-level semantic diff (rows aligned by content, moves
            tracked, renames recognized, quoting/whitespace-only changes invisible).
            Rows are labelled by an auto-detected discriminating column. Exit codes:
            0 identical, 1 different, 2 error.

            install registers the merge driver and the diff driver in git config;
            add "*.csv merge=csvmerge diff=csvmerge" to .gitattributes to activate
            them, after which git diff / log -p show semantic CSV diffs.
            """);
    }
}
