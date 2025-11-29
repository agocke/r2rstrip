using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ILVerify;
using Xunit.Abstractions;

namespace R2RStrip.Tests;

/// <summary>
/// Helper utilities for building and testing assemblies
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Run a command-line executable and capture output
    /// </summary>
    public static async Task<CommandResult> RunCommand(string executable, string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception($"Failed to start {executable}");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            Output = output + error
        };
    }

    /// <summary>
    /// Run a .NET assembly and capture output
    /// </summary>
    public static async Task<CommandResult> RunDll(string dllPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\"",
            WorkingDirectory = Path.GetDirectoryName(dllPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception($"Failed to start dotnet");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            Output = output.Trim() + (string.IsNullOrEmpty(error) ? "" : "\n" + error.Trim())
        };
    }

    /// <summary>
    /// Check if an assembly is R2R (has ManagedNativeHeader)
    /// </summary>
    public static bool IsR2RAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var corHeader = peReader.PEHeaders.CorHeader;
        return corHeader?.ManagedNativeHeaderDirectory.Size > 0;
    }

    /// <summary>
    /// Check if a file is a valid PE file with metadata
    /// </summary>
    public static bool IsValidManagedPE(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);

            // Must have valid PE headers
            if (peReader.PEHeaders == null) return false;

            // Must have metadata
            if (!peReader.HasMetadata) return false;

            // Try to read metadata (this validates the metadata structure)
            var metadataReader = peReader.GetMetadataReader();
            return metadataReader != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get metadata information about an assembly
    /// </summary>
    public static AssemblyMetadataInfo GetMetadataInfo(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var info = new AssemblyMetadataInfo
        {
            TypeDefCount = metadataReader.TypeDefinitions.Count,
            TypeRefCount = metadataReader.TypeReferences.Count,
            MethodDefCount = metadataReader.MethodDefinitions.Count,
            FieldDefCount = metadataReader.FieldDefinitions.Count,
            MemberRefCount = metadataReader.MemberReferences.Count,
            AssemblyRefCount = metadataReader.AssemblyReferences.Count,
            CustomAttributeCount = metadataReader.CustomAttributes.Count,
            TypeNames = new List<string>(),
            MethodNames = new List<string>()
        };

        // Collect type names
        foreach (var typeDefHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeDefHandle);
            var typeName = metadataReader.GetString(typeDef.Name);
            if (!string.IsNullOrEmpty(typeName))
            {
                info.TypeNames.Add(typeName);
            }
        }

        // Collect method names
        foreach (var methodDefHandle in metadataReader.MethodDefinitions)
        {
            var methodDef = metadataReader.GetMethodDefinition(methodDefHandle);
            var methodName = metadataReader.GetString(methodDef.Name);
            if (!string.IsNullOrEmpty(methodName))
            {
                info.MethodNames.Add(methodName);
            }
        }

        return info;
    }

    /// <summary>
    /// Compare metadata between two assemblies to ensure they have the same structure
    /// </summary>
    public static void AssertMetadataMatches(string expectedPath, string actualPath)
    {
        var expected = GetMetadataInfo(expectedPath);
        var actual = GetMetadataInfo(actualPath);

        // Compare counts
        if (expected.TypeDefCount != actual.TypeDefCount)
            throw new Exception($"TypeDef count mismatch: expected {expected.TypeDefCount}, got {actual.TypeDefCount}");

        if (expected.MethodDefCount != actual.MethodDefCount)
            throw new Exception($"MethodDef count mismatch: expected {expected.MethodDefCount}, got {actual.MethodDefCount}");

        if (expected.FieldDefCount != actual.FieldDefCount)
            throw new Exception($"FieldDef count mismatch: expected {expected.FieldDefCount}, got {actual.FieldDefCount}");

        // Compare type names
        var missingTypes = expected.TypeNames.Except(actual.TypeNames).ToList();
        var extraTypes = actual.TypeNames.Except(expected.TypeNames).ToList();

        if (missingTypes.Any())
            throw new Exception($"Missing types: {string.Join(", ", missingTypes)}");

        if (extraTypes.Any())
            throw new Exception($"Extra types: {string.Join(", ", extraTypes)}");

        // Compare method names
        var missingMethods = expected.MethodNames.Except(actual.MethodNames).ToList();
        var extraMethods = actual.MethodNames.Except(expected.MethodNames).ToList();

        if (missingMethods.Any())
            throw new Exception($"Missing methods: {string.Join(", ", missingMethods)}");

        if (extraMethods.Any())
            throw new Exception($"Extra methods: {string.Join(", ", extraMethods)}");
    }

    /// <summary>
    /// Strip an R2R assembly by calling Program.Main directly
    /// </summary>
    public static int StripAssembly(string inputPath, string outputPath, bool verbose = false)
    {
        Program.StripR2R(inputPath, outputPath, verbose);
        return 0;
    }

    /// <summary>
    /// Verify IL using ILVerify
    /// </summary>
    public static List<string> VerifyIL(string assemblyPath)
    {
        var errors = new List<string>();

        try
        {
            var resolver = new SimpleResolver();

            // Add reference assemblies from .NET SDK
            var refAssemblyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/dnvm/dn/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0");

            if (Directory.Exists(refAssemblyPath))
            {
                foreach (var dll in Directory.GetFiles(refAssemblyPath, "*.dll"))
                {
                    resolver.AddReference(dll);
                }
            }

            var verifier = new Verifier(resolver);
            verifier.SetSystemModuleName(new AssemblyNameInfo("System.Runtime"));

            using var peReader = new PEReader(File.OpenRead(assemblyPath));
            var results = verifier.Verify(peReader);

            foreach (var result in results)
            {
                if (result.Code != VerifierError.None)
                {
                    errors.Add($"{result.Code}: {result.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Verification failed: {ex.Message}");
        }

        return errors;
    }

    /// <summary>
    /// Extract the raw #Strings heap from an assembly's metadata.
    /// Uses the public MetadataReaderExtensions API to locate the heap.
    /// </summary>
    public static byte[] GetStringsHeap(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        // Use the public API to get the heap offset and size
        int heapOffset = metadataReader.GetHeapMetadataOffset(HeapIndex.String);
        int heapSize = metadataReader.GetHeapSize(HeapIndex.String);

        // Extract the heap bytes from the metadata block
        var metadataBlock = peReader.GetMetadata();
        var metadataBytes = metadataBlock.GetContent().ToArray();

        var result = new byte[heapSize];
        Array.Copy(metadataBytes, heapOffset, result, 0, heapSize);
        return result;
    }

    /// <summary>
    /// Enumerate all strings from the #Strings heap
    /// </summary>
    public static List<string> EnumerateStrings(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        var strings = new List<string>();
        var currentHandle = default(StringHandle);

        while (true)
        {
            currentHandle = reader.GetNextHandle(currentHandle);
            if (currentHandle.IsNil)
                break;

            strings.Add(reader.GetString(currentHandle));
        }

        return strings;
    }

    /// <summary>
    /// Compare #Strings heaps between two assemblies
    /// </summary>
    public static void AssertStringHeapsMatch(string expectedPath, string actualPath, ITestOutputHelper? output = null)
    {
        var expectedHeap = GetStringsHeap(expectedPath);
        var actualHeap = GetStringsHeap(actualPath);

        // Dump the string tables for debugging
        if (output != null)
        {
            output.WriteLine($"\n=== Input Assembly ({Path.GetFileName(expectedPath)}) Strings ===");
            var expectedStrings = EnumerateStrings(expectedPath);
            for (int i = 0; i < expectedStrings.Count; i++)
            {
                output.WriteLine($"  [{i}] \"{expectedStrings[i]}\"");
            }

            output.WriteLine($"\n=== Output Assembly ({Path.GetFileName(actualPath)}) Strings ===");
            var actualStrings = EnumerateStrings(actualPath);
            for (int i = 0; i < actualStrings.Count; i++)
            {
                output.WriteLine($"  [{i}] \"{actualStrings[i]}\"");
            }

            output.WriteLine($"\n=== Comparison ===");
            output.WriteLine($"Input heap size: {expectedHeap.Length} bytes, {expectedStrings.Count} strings");
            output.WriteLine($"Output heap size: {actualHeap.Length} bytes, {actualStrings.Count} strings");
        }

        if (!expectedHeap.SequenceEqual(actualHeap))
        {
            var msg = $"#Strings heap mismatch: expected {expectedHeap.Length} bytes, got {actualHeap.Length} bytes";

            // Find first difference
            var minLen = Math.Min(expectedHeap.Length, actualHeap.Length);
            for (int i = 0; i < minLen; i++)
            {
                if (expectedHeap[i] != actualHeap[i])
                {
                    msg += $"\nFirst difference at offset 0x{i:X4}: expected 0x{expectedHeap[i]:X2}, got 0x{actualHeap[i]:X2}";
                    break;
                }
            }

            throw new Exception(msg);
        }
    }
}

/// <summary>
/// Simple resolver for ILVerify
/// </summary>
internal class SimpleResolver : IResolver
{
    private readonly Dictionary<string, PEReader> _cache = new();

    public void AddReference(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!_cache.ContainsKey(name))
        {
            _cache[name] = new PEReader(File.OpenRead(path));
        }
    }

    public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName)
    {
        var name = assemblyName.Name ?? assemblyName.FullName;
        return _cache.TryGetValue(name, out var reader) ? reader : null;
    }

    public PEReader? ResolveModule(AssemblyNameInfo referencingAssembly, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return _cache.TryGetValue(name, out var reader) ? reader : null;
    }
}

/// <summary>
/// Information about a test assembly (IL-only, R2R, and stripped versions)
/// </summary>
internal class TestAssembly
{
    public required string Name { get; init; }
    public required string ILOnlyPath { get; init; }
    public required string R2RPath { get; init; }
    public required string StrippedPath { get; init; }
    public required string TestDirectory { get; init; }
}

/// <summary>
/// Result of running a command
/// </summary>
internal class CommandResult
{
    public required int ExitCode { get; init; }
    public required string Output { get; init; }
}

/// <summary>
/// Metadata information about an assembly
/// </summary>
internal class AssemblyMetadataInfo
{
    public int TypeDefCount { get; init; }
    public int TypeRefCount { get; init; }
    public int MethodDefCount { get; init; }
    public int FieldDefCount { get; init; }
    public int MemberRefCount { get; init; }
    public int AssemblyRefCount { get; init; }
    public int CustomAttributeCount { get; init; }
    public List<string> TypeNames { get; init; } = new();
    public List<string> MethodNames { get; init; } = new();
}
