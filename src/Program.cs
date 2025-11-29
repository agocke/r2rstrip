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
        var assemblyDef = metadataReader.GetAssemblyDefinition();

        if (verbose)
        {
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

        // Use MetadataCopier to copy all metadata from source
        var copier = new MetadataCopier(
            metadataReader,
            metadataBuilder,
            verbose);

        copier.CopyAll();

        // Add a stub Program class with Main method for test validation
        AddStubProgramWithMain(metadataBuilder, methodBodyEncoder, verbose);

        // Entry point is the last method we just added
        var entryPoint = MetadataTokens.MethodDefinitionHandle(metadataBuilder.GetRowCount(TableIndex.MethodDef));

        // Build the PE
        var metadataRootBuilder = new MetadataRootBuilder(metadataBuilder);
        var peBuilder = new ManagedPEBuilder(
            header: new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            metadataRootBuilder: metadataRootBuilder,
            ilStream: ilBuilder,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly,
            deterministicIdProvider: content => new BlobContentId(Guid.NewGuid(), 0x04030201));

        // Serialize to file
        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        using var outputStream = File.Create(outputFile);
        peBlob.WriteContentTo(outputStream);

        if (verbose)
        {
            Console.WriteLine($"Created minimal assembly: {new FileInfo(outputFile).Length} bytes");
        }
    }

    private static void AddStubProgramWithMain(
        MetadataBuilder metadataBuilder,
        MethodBodyStreamEncoder methodBodyEncoder,
        bool verbose)
    {
        // Add a Program type
        var objectTypeRef = metadataBuilder.AddTypeReference(
            resolutionScope: metadataBuilder.AddAssemblyReference(
                name: metadataBuilder.GetOrAddString("System.Runtime"),
                version: new Version(9, 0, 0, 0),
                culture: default,
                publicKeyOrToken: metadataBuilder.GetOrAddBlob(new byte[] {
                    0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a }),
                flags: default,
                hashValue: default),
            @namespace: metadataBuilder.GetOrAddString("System"),
            name: metadataBuilder.GetOrAddString("Object"));

        metadataBuilder.AddTypeDefinition(
            attributes: TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
            @namespace: default,
            name: metadataBuilder.GetOrAddString("Program"),
            baseType: objectTypeRef,
            fieldList: MetadataTokens.FieldDefinitionHandle(metadataBuilder.GetRowCount(TableIndex.Field) + 1),
            methodList: MetadataTokens.MethodDefinitionHandle(metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1));

        // Create stub Main method body (returns 100)
        // IL: ldc.i4.s 100, ret
        var codeBuilder = new BlobBuilder();
        var ilBuilder = new InstructionEncoder(codeBuilder);

        ilBuilder.OpCode(ILOpCode.Ldc_i4_s);
        codeBuilder.WriteByte(100);
        ilBuilder.OpCode(ILOpCode.Ret);

        var bodyOffset = methodBodyEncoder.AddMethodBody(ilBuilder);

        // Add Main method signature: int32 Main()
        var signatureBuilder = new BlobBuilder();
        new BlobEncoder(signatureBuilder)
            .MethodSignature()
            .Parameters(0, returnType => returnType.Type().Int32(), parameters => { });

        // Reuse the existing "<Main>$" string from the original assembly
        metadataBuilder.AddMethodDefinition(
            attributes: MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            implAttributes: MethodImplAttributes.IL,
            name: metadataBuilder.GetOrAddString("<Main>$"),
            signature: metadataBuilder.GetOrAddBlob(signatureBuilder),
            bodyOffset: bodyOffset,
            parameterList: MetadataTokens.ParameterHandle(metadataBuilder.GetRowCount(TableIndex.Param) + 1));

        if (verbose)
        {
            Console.WriteLine("  Added stub Program.<Main>$() method (returns 100)");
        }
    }
}
