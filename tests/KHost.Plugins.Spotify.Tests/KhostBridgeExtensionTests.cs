using System.Diagnostics;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// The extension runs inside Spotify's own JS engine, not .NET, so its only test surface is a
/// browser-shaped stub run under node. This just shells out and surfaces node's own failures; the
/// real assertions live in tests/extension/khost-bridge.test.mjs.
/// </summary>
public class KhostBridgeExtensionTests
{
    [RequiresNodeFact]
    public async Task TheExtensionsNodeSuitePasses()
    {
        var testFile = FindTestFile();

        var startInfo = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(testFile),
        };
        startInfo.ArgumentList.Add("--test");
        startInfo.ArgumentList.Add(testFile);

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"node --test failed (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }

    /// <summary>
    /// The JS suite deliberately lives outside any .NET project (no build step of its own), so it
    /// is not copied to the test binary's output directory the way a project item would be — this
    /// walks up from there to the checkout that still has it.
    /// </summary>
    private static string FindTestFile()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "extension", "khost-bridge.test.mjs");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("could not locate tests/extension/khost-bridge.test.mjs above " + AppContext.BaseDirectory);
    }
}

public sealed class RequiresNodeFactAttribute : FactAttribute
{
    public RequiresNodeFactAttribute()
    {
        if (!NodeIsInstalled.Value)
            Skip = "node is not installed";
    }

    private static readonly Lazy<bool> NodeIsInstalled = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });
}
