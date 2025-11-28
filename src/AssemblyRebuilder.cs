using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;


/// <summary>
/// Rebuilds an R2R assembly as an IL-only assembly with correct RVAs.
/// Uses System.Reflection.Metadata to completely rebuild the PE file.
/// </summary>
public class AssemblyRebuilder
{
    private readonly bool _verbose;
    private readonly TextWriter _output;

    public AssemblyRebuilder(bool verbose, TextWriter? output = null)
    {
        _verbose = verbose;
        _output = output ?? TextWriter.Null;
    }

    public void RebuildAsILOnly(string inputPath, string outputPath)
    {
        using var inputStream = File.OpenRead(inputPath);
        using var peReader = new PEReader(inputStream);

        if (!peReader.HasMetadata)
        {
            throw new InvalidOperationException("Input file does not contain managed metadata");
        }

        var metadataReader = peReader.GetMetadataReader();

        if (_verbose)
        {
            _output.WriteLine($"Reading R2R assembly: {Path.GetFileName(inputPath)}");
            var assemblyDef = metadataReader.GetAssemblyDefinition();
            _output.WriteLine($"  Assembly: {metadataReader.GetString(assemblyDef.Name)} v{assemblyDef.Version}");
        }

        // Create builders
        var metadataBuilder = new MetadataBuilder();
        var ilBuilder = new BlobBuilder();
        var methodBodyEncoder = new MethodBodyStreamEncoder(ilBuilder);

        if (_verbose)
        {
            _output.WriteLine("Copying metadata...");
        }

        // Build metadata and IL
        var copier = new MetadataCopier(metadataReader, metadataBuilder, methodBodyEncoder, peReader, _verbose);
        copier.CopyAll();

        if (_verbose)
        {
            _output.WriteLine("Building PE file...");
        }

        // Build the output PE
        var peBuilder = BuildPE(metadataReader, metadataBuilder, ilBuilder, peReader);

        // Write to file
        var peBlob = new BlobBuilder();
        var contentId = peBuilder.Serialize(peBlob);

        using (var outputStream = File.Create(outputPath))
        {
            peBlob.WriteContentTo(outputStream);
        }

        if (_verbose)
        {
            _output.WriteLine($"Output written: {new FileInfo(outputPath).Length} bytes");
        }
    }

    private ManagedPEBuilder BuildPE(
        MetadataReader metadataReader,
        MetadataBuilder metadataBuilder,
        BlobBuilder ilStream,
        PEReader peReader)
    {
        // Get assembly info
        var assemblyDef = metadataReader.GetAssemblyDefinition();

        // Build metadata root
        var metadataRootBuilder = new MetadataRootBuilder(metadataBuilder);

        // Create PE headers
        var peHeaderBuilder = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Bit32Machine);

        // Find entry point
        var entryPoint = default(MethodDefinitionHandle);
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
        {
            // The entry point in COR header is a token
            int token = (int)corHeader.EntryPointTokenOrRelativeVirtualAddress;
            var sourceHandle = MetadataTokens.EntityHandle(token);

            // Map it to our new metadata
            if (sourceHandle.Kind == HandleKind.MethodDefinition)
            {
                var sourceMethodHandle = (MethodDefinitionHandle)sourceHandle;
                // Find this in our handle map - but we need to use row number
                // For now, assume entry point is at the same row (this is a simplification)
                entryPoint = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(sourceMethodHandle));
            }
        }

        // Build the managed PE
        return new ManagedPEBuilder(
            peHeaderBuilder,
            metadataRootBuilder,
            ilStream,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: content => new BlobContentId(Guid.NewGuid(), 0x04030201));
    }
}