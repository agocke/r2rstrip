using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace R2RStrip;

/// <summary>
/// Provides methods to strip ReadyToRun (R2R) native code from .NET PE assemblies,
/// producing IL-only output with all metadata preserved.
/// </summary>
public static class R2RStripper
{
    /// <summary>
    /// Strips the R2R native code from <paramref name="inputFile"/> and writes an
    /// IL-only assembly to <paramref name="outputFile"/>.
    /// </summary>
    /// <param name="inputFile">Path to the R2R input assembly.</param>
    /// <param name="outputFile">Path for the IL-only output assembly.</param>
    /// <param name="verbose">When <see langword="true"/>, writes progress information to <see cref="Console"/>.</param>
    public static void Strip(string inputFile, string outputFile, bool verbose = false)
    {
        using var inputStream = File.OpenRead(inputFile);
        using var peReader = new PEReader(inputStream);
        var metadataReader = peReader.GetMetadataReader();

        if (verbose)
        {
            var assemblyDef = metadataReader.GetAssemblyDefinition();
            Console.WriteLine($"Reading assembly: {metadataReader.GetString(assemblyDef.Name)}");
        }

        // Find entry point from source
        var entryPointHandle = default(MethodDefinitionHandle);
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
        {
            int token = (int)corHeader.EntryPointTokenOrRelativeVirtualAddress;
            var sourceHandle = MetadataTokens.EntityHandle(token);
            if (sourceHandle.Kind == HandleKind.MethodDefinition)
            {
                entryPointHandle = (MethodDefinitionHandle)sourceHandle;
            }
        }

        // Create metadata builder and IL stream
        var metadataBuilder = new MetadataBuilder();
        var ilBuilder = new BlobBuilder();
        var methodBodyEncoder = new MethodBodyStreamEncoder(ilBuilder);
        var mappedFieldData = new BlobBuilder();
        var managedResources = new BlobBuilder();

        // Copy all metadata and IL bodies from source
        var copier = new MetadataCopier(
            metadataReader,
            metadataBuilder,
            peReader,
            methodBodyEncoder,
            mappedFieldData,
            managedResources,
            verbose);

        copier.CopyAll();

        // Build the PE
        var metadataRootBuilder = new MetadataRootBuilder(metadataBuilder);
        var peBuilder = new ManagedPEBuilder(
            header: new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            metadataRootBuilder: metadataRootBuilder,
            ilStream: ilBuilder,
            mappedFieldData: mappedFieldData,
            managedResources: managedResources,
            entryPoint: entryPointHandle,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: content => new BlobContentId(Guid.NewGuid(), 0x04030201));

        // Serialize to file
        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        using var outputStream = File.Create(outputFile);
        peBlob.WriteContentTo(outputStream);

        if (verbose)
        {
            Console.WriteLine($"Created IL-only assembly: {new FileInfo(outputFile).Length} bytes");
        }
    }
}
