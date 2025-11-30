using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace R2RStrip.Tests;

/// <summary>
/// Tests for the RawMetadataPEBuilder class to verify it can write metadata without MetadataBuilder
/// </summary>
public class RawMetadataPEBuilderTests
{
    [Fact]
    public void CreateMinimalPE_ShouldWriteValidPE()
    {
        // Arrange: Create a minimal RawMetadataPEBuilder
        var peHeaderBuilder = new PEHeaderBuilder(
            machine: Machine.Amd64,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);

        var peBuilder = new RawMetadataPEBuilder(
            header: peHeaderBuilder,
            corFlags: CorFlags.ILOnly,
            entryPoint: default);

        // Act: Serialize the PE
        var peBlob = new BlobBuilder();
        var contentId = peBuilder.Serialize(peBlob);

        // Assert: Should create a non-empty blob
        Assert.True(peBlob.Count > 0, "PE blob should not be empty");

        // Write to a temporary file and verify it's a valid PE
        var tempFile = Path.GetTempFileName();
        try
        {
            using (var stream = File.Create(tempFile))
            {
                peBlob.WriteContentTo(stream);
            }

            // Verify it's a valid PE file
            Assert.True(TestHelpers.IsValidManagedPE(tempFile), "Should be a valid managed PE file");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void SerializeTwice_ShouldProduceSameOutput()
    {
        // Arrange
        var peHeaderBuilder = new PEHeaderBuilder(
            machine: Machine.Amd64,
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll);

        var peBuilder = new RawMetadataPEBuilder(
            header: peHeaderBuilder,
            corFlags: CorFlags.ILOnly,
            entryPoint: default);

        // Act: Serialize twice
        var peBlob1 = new BlobBuilder();
        var contentId1 = peBuilder.Serialize(peBlob1);

        var peBlob2 = new BlobBuilder();
        var contentId2 = peBuilder.Serialize(peBlob2);

        // Assert: Should produce consistent output
        Assert.Equal(peBlob1.Count, peBlob2.Count);
    }
}

