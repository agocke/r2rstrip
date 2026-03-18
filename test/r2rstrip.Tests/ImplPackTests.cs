using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace R2RStrip.Tests;

/// <summary>
/// Strips every managed assembly in the Microsoft.NETCore.App implementation pack
/// and verifies the output IL is correct using ILVerify.
/// Only reports errors that are NEW (introduced by stripping), not pre-existing in the source.
/// </summary>
public class ImplPackTests
{
    private readonly ITestOutputHelper _output;

    public ImplPackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> GetImplPackAssemblies()
    {
        var implDir = FindImplPackDir();
        if (implDir == null) yield break;

        foreach (var dll in Directory.GetFiles(implDir, "*.dll").OrderBy(f => f))
        {
            bool hasMeta = false;
            try
            {
                using var stream = File.OpenRead(dll);
                using var pe = new PEReader(stream);
                hasMeta = pe.HasMetadata;
            }
            catch { /* skip non-PE files */ }

            if (hasMeta)
                yield return [Path.GetFileName(dll)];
        }
    }

    [Theory]
    [MemberData(nameof(GetImplPackAssemblies))]
    public void Assembly_StripsWithValidIL(string assemblyName)
    {
        var implDir = FindImplPackDir()!;
        var inputPath = Path.Combine(implDir, assemblyName);
        var outputPath = Path.GetTempFileName();

        try
        {
            Program.StripR2R(inputPath, outputPath);

            Assert.True(TestHelpers.IsValidManagedPE(outputPath),
                $"{assemblyName}: should produce a valid managed PE");

            Assert.False(TestHelpers.IsR2RAssembly(outputPath),
                $"{assemblyName}: should not be R2R after stripping");

            // Compare metadata table row counts — every table must match
            var tableDiffs = TestHelpers.CompareMetadataTables(inputPath, outputPath);
            if (tableDiffs.Count > 0)
            {
                _output.WriteLine($"{assemblyName}: metadata table mismatches:");
                foreach (var diff in tableDiffs)
                    _output.WriteLine($"  {diff}");
            }
            Assert.Empty(tableDiffs);

            // ILVerify both and compare — the error sets must be identical
            var baselineErrors = TestHelpers.VerifyIL(inputPath);
            var strippedErrors = TestHelpers.VerifyIL(outputPath);

            var baselineSorted = baselineErrors.OrderBy(e => e).ToList();
            var strippedSorted = strippedErrors.OrderBy(e => e).ToList();

            if (!baselineSorted.SequenceEqual(strippedSorted))
            {
                var baselineSet = baselineErrors.ToHashSet();
                var strippedSet = strippedErrors.ToHashSet();

                var added = strippedSorted.Where(e => !baselineSet.Contains(e)).ToList();
                var removed = baselineSorted.Where(e => !strippedSet.Contains(e)).ToList();

                _output.WriteLine($"{assemblyName}: ILVerify results differ (baseline: {baselineErrors.Count}, stripped: {strippedErrors.Count})");
                foreach (var e in added.Take(10))
                    _output.WriteLine($"  + {e}");
                foreach (var e in removed.Take(10))
                    _output.WriteLine($"  - {e}");
            }

            Assert.Equal(baselineSorted, strippedSorted);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string? FindImplPackDir()
    {
        var sharedBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/dnvm/dn/shared/Microsoft.NETCore.App");

        if (!Directory.Exists(sharedBase)) return null;

        return Directory.GetDirectories(sharedBase)
            .Where(d => !Path.GetFileName(d).Contains("preview"))
            .OrderByDescending(d => Path.GetFileName(d))
            .FirstOrDefault();
    }
}
