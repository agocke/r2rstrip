using System.Diagnostics;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace R2RStrip.Tests;

/// <summary>
/// End-to-end test: publish r2rstrip as self-contained, strip all R2R binaries
/// in the publish output, then run the stripped r2rstrip to prove it still works.
/// </summary>
public class SelfStripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _workDir;

    public SelfStripTests(ITestOutputHelper output)
    {
        _output = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"r2rstrip-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SelfContained_StripsAllThenRuns()
    {
        // Step 1: Publish r2rstrip as self-contained
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.True(repoRoot != null, "Could not find repository root");
        var srcDir = Path.Combine(repoRoot!, "src");
        var publishDir = Path.Combine(_workDir, "publish");

        _output.WriteLine($"Publishing self-contained to {publishDir}...");
        var publishResult = await TestHelpers.RunCommand(
            "dotnet", $"publish \"{srcDir}\" -c Release -r linux-x64 --self-contained -o \"{publishDir}\"",
            srcDir);
        Assert.True(publishResult.ExitCode == 0, $"Publish failed:\n{publishResult.Output}");

        // Step 2: Find all managed DLLs that are R2R
        var r2rDlls = new List<string>();
        var allDlls = Directory.GetFiles(publishDir, "*.dll");
        foreach (var dll in allDlls)
        {
            try
            {
                if (TestHelpers.IsR2RAssembly(dll))
                    r2rDlls.Add(dll);
            }
            catch { }
        }

        _output.WriteLine($"Found {r2rDlls.Count} R2R assemblies out of {allDlls.Length} total DLLs");
        Assert.True(r2rDlls.Count > 0, "Should have some R2R assemblies in self-contained publish");

        // Step 3: Use the published r2rstrip to strip itself
        var apphost = Path.Combine(publishDir, "r2rstrip");
        Assert.True(File.Exists(apphost), "Apphost should exist");

        // Phase 1: Strip all R2R assemblies to a staging directory
        var stagingDir = Path.Combine(_workDir, "staging");
        Directory.CreateDirectory(stagingDir);

        int stripped = 0;
        int failed = 0;
        foreach (var dll in r2rDlls)
        {
            var stagedOutput = Path.Combine(stagingDir, Path.GetFileName(dll));
            var result = await TestHelpers.RunCommand(apphost, $"\"{dll}\" \"{stagedOutput}\"", publishDir);
            if (result.ExitCode == 0)
            {
                stripped++;
            }
            else
            {
                _output.WriteLine($"  FAIL: {Path.GetFileName(dll)}: {result.Output.Trim()}");
                failed++;
            }
        }

        _output.WriteLine($"Stripped {stripped}/{r2rDlls.Count} assemblies ({failed} failed)");
        Assert.Equal(0, failed);

        // Phase 2: Copy all stripped assemblies back over the originals
        foreach (var dll in r2rDlls)
        {
            var stagedOutput = Path.Combine(stagingDir, Path.GetFileName(dll));
            File.Copy(stagedOutput, dll, overwrite: true);
        }

        // Verify none are R2R anymore
        foreach (var dll in r2rDlls)
        {
            Assert.False(TestHelpers.IsR2RAssembly(dll),
                $"{Path.GetFileName(dll)} should no longer be R2R");
        }

        // Step 4: Run the stripped r2rstrip on a test assembly to prove it works
        // Use one of the stripped DLLs as input (strip it again — should be a no-op)
        var testInput = r2rDlls.First();
        var testOutput = Path.Combine(_workDir, "re-stripped.dll");

        _output.WriteLine($"Running stripped r2rstrip on {Path.GetFileName(testInput)}...");
        var runResult = await TestHelpers.RunCommand(
            apphost, $"\"{testInput}\" \"{testOutput}\"", publishDir);

        _output.WriteLine($"Exit code: {runResult.ExitCode}");
        _output.WriteLine(runResult.Output);

        Assert.Equal(0, runResult.ExitCode);
        Assert.True(File.Exists(testOutput), "Re-stripped output should exist");
        Assert.True(TestHelpers.IsValidManagedPE(testOutput), "Re-stripped output should be valid PE");
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
