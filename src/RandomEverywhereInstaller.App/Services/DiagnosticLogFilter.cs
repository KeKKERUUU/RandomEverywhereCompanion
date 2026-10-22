using System.IO;

namespace RandomEverywhereInstaller.App.Services;

public static class DiagnosticLogFilter
{
    public static string Apply(string text, string level)
    {
        if (level == "all") return text;
        var result = new List<string>();
        var severity = "info";
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var timestamp = line.Length > 8 && line[2] == ':' && line[5] == ':' && line[8] == ' ';
            var body = timestamp ? line[9..] : line;
            if (body.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)) severity = "error";
            else if (body.StartsWith("[WARN]", StringComparison.OrdinalIgnoreCase)) severity = "warning";
            else if (timestamp || body.StartsWith("[INFO]", StringComparison.OrdinalIgnoreCase)) severity = "info";
            // Continuation lines inherit their entry's severity, retaining stacks/details.
            if (severity == "error" || level == "warnings" && severity == "warning") result.Add(line);
        }
        return string.Join(Environment.NewLine, result);
    }
}
