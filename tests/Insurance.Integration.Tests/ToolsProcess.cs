using System.Diagnostics;

namespace Insurance.Integration.Tests;

/// <summary>
/// The admin command line (src/Insurance.Tools) run as a process of its own, with an environment of its own: a connection or a
/// password given to it is never set in the tests' process, where another test could see it. The tools' build is copied next to
/// the tests (project reference); it runs on the same dotnet host as the tests.
/// </summary>
public static class ToolsProcess
{
    public sealed record Run(int Exit, string Output, string Error);

    /// <param name="environment">Variables set for the command; a null value removes the variable.</param>
    public static async Task<Run> RunAsync(IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var tools = Path.Combine(AppContext.BaseDirectory, "Insurance.Tools.dll");
        Assert.True(File.Exists(tools), $"{tools} is not there");
        // The same dotnet host that runs the tests (testhost runs under it); "dotnet" from PATH otherwise.
        var host = Environment.ProcessPath is { } h && Path.GetFileNameWithoutExtension(h) == "dotnet" ? h : "dotnet";
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(tools);
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var (name, value) in environment)
            if (value is null) start.Environment.Remove(name);
            else start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new Run(process.ExitCode, await output, await error);
    }
}
