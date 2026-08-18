using System.Diagnostics;

namespace WebDataStudio.Server.Admin;

public sealed record ProcessResult(int ExitCode, string StandardError);

/// Runs an external tool with an argument array — never a shell string — so nothing in a
/// connection string can be interpreted as a command. Passwords go through the environment,
/// because arguments are visible to every process on the machine.
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, Stream? output, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) info.Environment[key] = value;

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            return new ProcessResult(-1, $"could not start '{file}': {e.Message}");
        }

        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var copyTask = output is null
            ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct)
            : process.StandardOutput.BaseStream.CopyToAsync(output, ct);

        try
        {
            await Task.WhenAll(copyTask, errorTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled backup must not leave the tool running against the database.
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            throw;
        }

        return new ProcessResult(process.ExitCode, await errorTask);
    }

    public static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, Stream input, bool feedStdin,
        CancellationToken ct)
    {
        if (!feedStdin) return await RunAsync(file, arguments, environment, null, ct);

        var info = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) info.Environment[key] = value;

        using var process = new Process { StartInfo = info };

        try { process.Start(); }
        catch (Exception e) { return new ProcessResult(-1, $"could not start '{file}': {e.Message}"); }

        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await input.CopyToAsync(process.StandardInput.BaseStream, ct);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct);
        return new ProcessResult(process.ExitCode, await errorTask);
    }
}
