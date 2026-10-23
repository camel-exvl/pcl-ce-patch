using System.Text.Json;

namespace PclCeRestore;

public static class Program
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var options = ParseOptions(args.Skip(1).ToArray());
            string Get(string key) => options.TryGetValue(key, out var value)
                ? value : throw new ArgumentException($"Missing --{key}.");
            object result = args[0] switch
            {
                "inspect" => Patcher.Inspect(Get("input")),
                "patch" => Patcher.Patch(Get("input"), Get("output"), Get("feed-base"),
                    int.Parse(Get("revision")), Get("profile")),
                "verify" => Patcher.Verify(Get("original"), Get("patched"), Get("feed-base"),
                    int.Parse(Get("revision")), Get("profile")),
                _ => throw new ArgumentException(Usage)
            };
            var json = JsonSerializer.Serialize(result, JsonOptions);
            if (options.TryGetValue("report", out var report))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, json + "\n");
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception error)
        {
            // Do not dump IL, string heaps, configuration, or arbitrary exception messages from parsers.
            Console.Error.WriteLine(error is PatchException or ArgumentException or FormatException
                ? error.Message : $"Operation failed ({error.GetType().Name}). No output is approved for publication.");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException(Usage);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !result.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Invalid or repeated option.");
        }
        return result;
    }

    private const string Usage = "Commands: inspect --input EXE [--report JSON]; " +
        "patch --input EXE --output EXE --feed-base HTTPS_URL --revision N --profile JSON [--report JSON]; " +
        "verify --original EXE --patched EXE --feed-base HTTPS_URL --revision N --profile JSON [--report JSON]";
}

public sealed class PatchException(string message) : Exception(message);
