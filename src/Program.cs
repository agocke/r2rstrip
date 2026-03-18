using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

public class Program
{
    public static int Main(string[] args)
    {
        bool verbose = args.Contains("--verbose") || args.Contains("-v");
        var fileArgs = args.Where(a => !a.StartsWith("-")).ToArray();

        if (fileArgs.Length != 2)
        {
            Console.WriteLine("Usage: r2rstrip [options] <input-r2r-file> <output-file>");
            Console.WriteLine("Rebuilds an R2R assembly as IL-only with correct offsets");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -v, --verbose    Show detailed progress information");
            return 1;
        }

        string inputFile = fileArgs[0];
        string outputFile = fileArgs[1];

        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"Error: Input R2R file '{inputFile}' not found");
            return 1;
        }

        try
        {
            StripR2R(inputFile, outputFile, verbose);
            Console.WriteLine($"Successfully rebuilt '{inputFile}' as IL-only assembly: '{outputFile}'");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    public static void StripR2R(string inputFile, string outputFile, bool verbose = false)
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

        // Copy all metadata and IL bodies from source
        var copier = new MetadataCopier(
            metadataReader,
            metadataBuilder,
            peReader,
            methodBodyEncoder,
            verbose);

        copier.CopyAll();

        // Build the PE
        var metadataRootBuilder = new MetadataRootBuilder(metadataBuilder);
        var peBuilder = new ManagedPEBuilder(
            header: new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            metadataRootBuilder: metadataRootBuilder,
            ilStream: ilBuilder,
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
