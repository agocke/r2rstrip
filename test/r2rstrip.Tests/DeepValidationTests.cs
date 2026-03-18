using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace R2RStrip.Tests;

/// <summary>
/// Deep byte-level validation that stripping only removes R2R native code
/// and preserves everything else exactly. Runs on a single representative assembly.
/// </summary>
public class DeepValidationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _inputPath;
    private readonly string _outputPath;

    public DeepValidationTests(ITestOutputHelper output)
    {
        _output = output;

        // Pick System.Collections.dll — a mid-sized assembly with generics,
        // interfaces, nested types, and nontrivial IL.
        var dotnetRoot = TestHelpers.FindDotnetRoot()!;
        var sharedDir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
        var versionDir = Directory.GetDirectories(sharedDir)
            .Where(d => !Path.GetFileName(d).Contains("preview"))
            .OrderByDescending(d => Path.GetFileName(d))
            .First();

        _inputPath = Path.Combine(versionDir, "System.Collections.dll");
        _outputPath = Path.GetTempFileName();

        Program.StripR2R(_inputPath, _outputPath);
    }

    public void Dispose()
    {
        File.Delete(_outputPath);
    }

    [Fact]
    public void StrippedFile_IsSmallerThanOriginal()
    {
        var originalSize = new FileInfo(_inputPath).Length;
        var strippedSize = new FileInfo(_outputPath).Length;

        _output.WriteLine($"Original: {originalSize:N0} bytes");
        _output.WriteLine($"Stripped: {strippedSize:N0} bytes");
        _output.WriteLine($"Reduction: {originalSize - strippedSize:N0} bytes ({100.0 * (originalSize - strippedSize) / originalSize:F1}%)");

        Assert.True(strippedSize < originalSize,
            "Stripped assembly should be smaller (no native R2R code)");
    }

    [Fact]
    public void StrippedFile_HasNoManagedNativeHeader()
    {
        using var stream = File.OpenRead(_outputPath);
        using var pe = new PEReader(stream);
        var cor = pe.PEHeaders.CorHeader!;

        Assert.Equal(0, cor.ManagedNativeHeaderDirectory.Size);
        Assert.True((cor.Flags & CorFlags.ILOnly) != 0, "Should have ILOnly flag");
    }

    [Fact]
    public void StrippedFile_PreservesAllILBytesExactly()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        int compared = 0;
        int skippedNoBody = 0;
        var origMethods = origReader.MethodDefinitions.ToArray();
        var strippedMethods = strippedReader.MethodDefinitions.ToArray();

        Assert.Equal(origMethods.Length, strippedMethods.Length);

        for (int i = 0; i < origMethods.Length; i++)
        {
            var origMethod = origReader.GetMethodDefinition(origMethods[i]);
            var strippedMethod = strippedReader.GetMethodDefinition(strippedMethods[i]);

            // Names must match
            Assert.Equal(
                origReader.GetString(origMethod.Name),
                strippedReader.GetString(strippedMethod.Name));

            if (origMethod.RelativeVirtualAddress == 0)
            {
                skippedNoBody++;
                continue;
            }

            var origBody = origPe.GetMethodBody(origMethod.RelativeVirtualAddress);
            var strippedBody = strippedPe.GetMethodBody(strippedMethod.RelativeVirtualAddress);

            var origIL = origBody.GetILBytes()!;
            var strippedIL = strippedBody.GetILBytes()!;

            Assert.True(origIL.SequenceEqual(strippedIL),
                $"IL mismatch in method #{i} ({origReader.GetString(origMethod.Name)}): " +
                $"orig {origIL.Length} bytes, stripped {strippedIL.Length} bytes");

            Assert.Equal(origBody.MaxStack, strippedBody.MaxStack);
            Assert.Equal(origBody.LocalVariablesInitialized, strippedBody.LocalVariablesInitialized);
            Assert.Equal(origBody.ExceptionRegions.Length, strippedBody.ExceptionRegions.Length);

            compared++;
        }

        _output.WriteLine($"Compared {compared} method bodies byte-for-byte, {skippedNoBody} abstract/extern");
    }

    [Fact]
    public void StrippedFile_PreservesMethodSignatures()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        var origMethods = origReader.MethodDefinitions.ToArray();
        var strippedMethods = strippedReader.MethodDefinitions.ToArray();

        for (int i = 0; i < origMethods.Length; i++)
        {
            var origMethod = origReader.GetMethodDefinition(origMethods[i]);
            var strippedMethod = strippedReader.GetMethodDefinition(strippedMethods[i]);

            var origSig = origReader.GetBlobBytes(origMethod.Signature);
            var strippedSig = strippedReader.GetBlobBytes(strippedMethod.Signature);

            Assert.True(origSig.SequenceEqual(strippedSig),
                $"Signature mismatch in method #{i} ({origReader.GetString(origMethod.Name)})");
        }
    }

    [Fact]
    public void StrippedFile_PreservesFieldSignatures()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        var origFields = origReader.FieldDefinitions.ToArray();
        var strippedFields = strippedReader.FieldDefinitions.ToArray();

        Assert.Equal(origFields.Length, strippedFields.Length);

        for (int i = 0; i < origFields.Length; i++)
        {
            var origField = origReader.GetFieldDefinition(origFields[i]);
            var strippedField = strippedReader.GetFieldDefinition(strippedFields[i]);

            Assert.Equal(
                origReader.GetString(origField.Name),
                strippedReader.GetString(strippedField.Name));

            var origSig = origReader.GetBlobBytes(origField.Signature);
            var strippedSig = strippedReader.GetBlobBytes(strippedField.Signature);

            Assert.True(origSig.SequenceEqual(strippedSig),
                $"Field signature mismatch: {origReader.GetString(origField.Name)}");
        }
    }

    [Fact]
    public void StrippedFile_PreservesCustomAttributeBlobs()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        var origAttrs = origReader.CustomAttributes.ToArray();
        var strippedAttrs = strippedReader.CustomAttributes.ToArray();

        Assert.Equal(origAttrs.Length, strippedAttrs.Length);

        int compared = 0;
        for (int i = 0; i < origAttrs.Length; i++)
        {
            var origAttr = origReader.GetCustomAttribute(origAttrs[i]);
            var strippedAttr = strippedReader.GetCustomAttribute(strippedAttrs[i]);

            var origBlob = origReader.GetBlobBytes(origAttr.Value);
            var strippedBlob = strippedReader.GetBlobBytes(strippedAttr.Value);

            Assert.True(origBlob.SequenceEqual(strippedBlob),
                $"CustomAttribute #{i} value blob mismatch");

            // Constructor token should reference same row
            Assert.Equal(
                MetadataTokens.GetToken(origAttr.Constructor),
                MetadataTokens.GetToken(strippedAttr.Constructor));

            compared++;
        }

        _output.WriteLine($"Compared {compared} custom attribute blobs");
    }

    [Fact]
    public void StrippedFile_PreservesGuidHeap()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        // MVID should match
        var origMvid = origReader.GetGuid(origReader.GetModuleDefinition().Mvid);
        var strippedMvid = strippedReader.GetGuid(strippedReader.GetModuleDefinition().Mvid);
        Assert.Equal(origMvid, strippedMvid);

        // Guid heap sizes should match
        Assert.Equal(
            origReader.GetHeapSize(HeapIndex.Guid),
            strippedReader.GetHeapSize(HeapIndex.Guid));
    }

    [Fact]
    public void StrippedFile_PreservesAssemblyIdentity()
    {
        using var origStream = File.OpenRead(_inputPath);
        using var origPe = new PEReader(origStream);
        var origReader = origPe.GetMetadataReader();

        using var strippedStream = File.OpenRead(_outputPath);
        using var strippedPe = new PEReader(strippedStream);
        var strippedReader = strippedPe.GetMetadataReader();

        var origAsm = origReader.GetAssemblyDefinition();
        var strippedAsm = strippedReader.GetAssemblyDefinition();

        Assert.Equal(origReader.GetString(origAsm.Name), strippedReader.GetString(strippedAsm.Name));
        Assert.Equal(origAsm.Version, strippedAsm.Version);
        Assert.Equal(origAsm.Flags, strippedAsm.Flags);
        Assert.Equal(origAsm.HashAlgorithm, strippedAsm.HashAlgorithm);

        // Assembly references should match exactly
        var origRefs = origReader.AssemblyReferences.Select(h => origReader.GetAssemblyReference(h)).ToArray();
        var strippedRefs = strippedReader.AssemblyReferences.Select(h => strippedReader.GetAssemblyReference(h)).ToArray();

        Assert.Equal(origRefs.Length, strippedRefs.Length);
        for (int i = 0; i < origRefs.Length; i++)
        {
            Assert.Equal(origReader.GetString(origRefs[i].Name), strippedReader.GetString(strippedRefs[i].Name));
            Assert.Equal(origRefs[i].Version, strippedRefs[i].Version);
        }
    }
}
