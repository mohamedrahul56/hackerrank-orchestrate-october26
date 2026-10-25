using System.Text;

namespace Auditor.Tools;

/// <summary>
/// Development tools of the auditor, one executable with sub-commands (run from the repository root):
///   dotnet run --project code/tools/Auditor.Tools -c Release -- score --pred output.csv [--gold dataset/sample_output.csv] [--split both] [--markdown report.md]
///   ... -- package [--dry-run] [--require-final]      builds code.zip after checking every submission rule
///   ... -- usage-report [--final]                     model usage report from the usage log of the last run
///   ... -- final-check [--build-zip]
///   ... -- show-readings [--text] [--only FILE_ID ...]   screenshots next to what the model read (needs ANTHROPIC_API_KEY in the environment)
///   ... -- extra-cases                                writes evaluation/extra_cases.md and extra_cases_results.md
///   ... -- evaluation-report                          writes evaluation/evaluation_report.md from live measurements
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var stdout = Console.Out; var stderr = Console.Error;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            stdout.WriteLine("usage: Auditor.Tools <score|package|usage-report|final-check|extra-cases|evaluation-report> [options]");
            return args.Length == 0 ? 2 : 0;
        }
        var rest = args[1..];
        return args[0] switch
        {
            "score" => ScoreMain(rest, stdout),
            "package" => Packager.Run(rest, stdout, stderr),
            "usage-report" => UsageReport.Run(rest, stdout, stderr),
            "final-check" => FinalCheck.Run(rest, stdout, stderr),
            "extra-cases" => ExtraCases.Run(rest, stdout, stderr),
            "show-readings" => ShowReadings.Run(rest, stdout, stderr),
            "baselines" => Baselines.Run(rest, stdout, stderr),
            "agent-proofs" => AgentProofs.Run(rest, stdout, stderr),
            "evaluation-report" => EvaluationReport.Run(rest, stdout, stderr),
            "performance-report" => PerformanceReport.Run(rest, stdout, stderr),
            "mutation-check" => MutationCheck.Run(rest, stdout, stderr),
            "perf-run" => PerformanceReport.Run(args, stdout, stderr),
            _ => Unknown(args[0], stderr),
        };
    }

    private static int Unknown(string cmd, TextWriter err) { err.WriteLine($"error: unknown command {cmd}"); return 2; }

    private static int ScoreMain(string[] args, TextWriter stdout)
    {
        var root = Cli.Cli.RepoRoot();
        string? pred = null, md = null, label = null;
        string gold = Path.Combine(root, "dataset", "sample_output.csv"), split = "both";
        int failures = 20;
        bool reveal = false;
        for (int i = 0; i < args.Length; i++)
        {
            string V() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--pred": pred = V(); break;
                case "--gold": gold = V(); break;
                case "--split": split = V(); break;
                case "--failures": failures = int.Parse(V(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--reveal-heldout": reveal = true; break;
                case "--markdown": md = V(); break;
                case "--label": label = V(); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        if (pred is null) { Console.Error.WriteLine("error: --pred is required"); return 2; }
        var (text, _) = Scorer.BuildReport(Scorer.ReadRows(gold), Scorer.ReadRows(pred), split, failures, reveal, label ?? Path.GetFileName(pred));
        stdout.WriteLine(text);
        if (md is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(md))!);
            File.WriteAllText(md, text + "\n", new UTF8Encoding(false));
        }
        return 0;
    }
}
