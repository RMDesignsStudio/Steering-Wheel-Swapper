using System.Diagnostics;

namespace WheelSwap.Tests;

/// <summary>Runs second_reader.py (an independent Python ERP reader) on a written .serps.</summary>
static class SecondReader
{
    static bool? _available;

    public static bool Available
    {
        get
        {
            if (_available is null)
            {
                try { _available = Run("-P", "-c", "import zstandard").Code == 0; }
                catch { _available = false; }
            }
            return _available.Value;
        }
    }

    public static (int Code, string Output) Check(string serps, string template, IEnumerable<string> privateTokens, IEnumerable<string> entries)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "second_reader.py");
        return Run(["-P", script, serps, template, string.Join(",", privateTokens), .. entries]);
    }

    static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("py") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-3");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (o + err.Result).Trim());
    }
}
