using System.Diagnostics;

namespace KHost.Plugins.Spotify.Control;

public record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Whichever stream said something, for a message a host can act on.</summary>
    public string Message => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

/// <summary>Runs the small command-line tools the backends drive Spotify through.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Every transport command is a local IPC round trip that should take milliseconds. A backend
    /// hanging here would hang the console between singers, so it is cut off instead.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }
}
