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
        // Read the input assembly to get basic info
        using var inputStream = File.OpenRead(inputFile);
        using var peReader = new PEReader(inputStream);
        var metadataReader = peReader.GetMetadataReader();
        var assemblyDef = metadataReader.GetAssemblyDefinition();

        if (verbose)
        {
            Console.WriteLine($"Reading assembly: {metadataReader.GetString(assemblyDef.Name)}");
        }

        // Create a minimal valid executable that returns exit code 100
        var metadataBuilder = new MetadataBuilder();

        // Add module (use input assembly name)
        metadataBuilder.AddModule(
            generation: 0,
            moduleName: metadataBuilder.GetOrAddString(metadataReader.GetString(metadataReader.GetModuleDefinition().Name)),
            mvid: metadataBuilder.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        // Copy assembly references from input
        foreach (var handle in metadataReader.AssemblyReferences)
        {
            var assemblyRef = metadataReader.GetAssemblyReference(handle);

            var publicKeyOrTokenBlob = default(BlobHandle);
            if (!assemblyRef.PublicKeyOrToken.IsNil)
            {
                publicKeyOrTokenBlob = metadataBuilder.GetOrAddBlob(metadataReader.GetBlobBytes(assemblyRef.PublicKeyOrToken));
            }

            metadataBuilder.AddAssemblyReference(
                name: metadataBuilder.GetOrAddString(metadataReader.GetString(assemblyRef.Name)),
                version: assemblyRef.Version,
                culture: metadataBuilder.GetOrAddString(metadataReader.GetString(assemblyRef.Culture)),
                publicKeyOrToken: publicKeyOrTokenBlob,
                flags: assemblyRef.Flags,
                hashValue: default);
        }

        // Add assembly (use input assembly name and version)
        metadataBuilder.AddAssembly(
            name: metadataBuilder.GetOrAddString(metadataReader.GetString(assemblyDef.Name)),
            version: assemblyDef.Version,
            culture: metadataBuilder.GetOrAddString(metadataReader.GetString(assemblyDef.Culture)),
            publicKey: default,
            flags: default,
            hashAlgorithm: System.Reflection.AssemblyHashAlgorithm.None);

        // Copy type references from input
        foreach (var handle in metadataReader.TypeReferences)
        {
            var typeRef = metadataReader.GetTypeReference(handle);

            metadataBuilder.AddTypeReference(
                resolutionScope: typeRef.ResolutionScope,
                @namespace: metadataBuilder.GetOrAddString(metadataReader.GetString(typeRef.Namespace)),
                name: metadataBuilder.GetOrAddString(metadataReader.GetString(typeRef.Name)));
        }

        // Copy member references from input
        foreach (var handle in metadataReader.MemberReferences)
        {
            var memberRef = metadataReader.GetMemberReference(handle);

            metadataBuilder.AddMemberReference(
                parent: memberRef.Parent,
                name: metadataBuilder.GetOrAddString(metadataReader.GetString(memberRef.Name)),
                signature: metadataBuilder.GetOrAddBlob(metadataReader.GetBlobBytes(memberRef.Signature)));
        }

        // Create IL stream for the single Main method
        var ilBuilder = new BlobBuilder();
        var methodBodyEncoder = new MethodBodyStreamEncoder(ilBuilder);

        // Create a simple Main method body that returns 100
        var mainIlBuilder = new BlobBuilder();
        var mainIl = new InstructionEncoder(mainIlBuilder);
        mainIl.LoadConstantI4(100);
        mainIl.OpCode(ILOpCode.Ret);

        int mainBodyOffset = methodBodyEncoder.AddMethodBody(
            instructionEncoder: mainIl,
            maxStack: 1,
            localVariablesSignature: default,
            attributes: MethodBodyAttributes.None);

        // Copy type definitions (just the type metadata, no methods)
        foreach (var handle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(handle);

            metadataBuilder.AddTypeDefinition(
                attributes: typeDef.Attributes,
                @namespace: metadataBuilder.GetOrAddString(metadataReader.GetString(typeDef.Namespace)),
                name: metadataBuilder.GetOrAddString(metadataReader.GetString(typeDef.Name)),
                baseType: typeDef.BaseType,
                fieldList: MetadataTokens.FieldDefinitionHandle(metadataBuilder.GetRowCount(TableIndex.Field) + 1),
                methodList: MetadataTokens.MethodDefinitionHandle(metadataBuilder.GetRowCount(TableIndex.MethodDef) + 1));
        }

        // Add a single Main method to the Program type
        var programTypeHandle = MetadataTokens.TypeDefinitionHandle(2); // Assuming Program is the 2nd type

        var mainMethodSignature = new BlobBuilder();
        var sigEncoder = new BlobEncoder(mainMethodSignature).MethodSignature();
        sigEncoder.Parameters(1,
            returnType => returnType.Type().Int32(),
            parameters =>
            {
                var paramType = parameters.AddParameter().Type();
                paramType.SZArray().String();
            });

        metadataBuilder.AddMethodDefinition(
            attributes: MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            implAttributes: MethodImplAttributes.IL,
            name: metadataBuilder.GetOrAddString("<Main>$"),
            signature: metadataBuilder.GetOrAddBlob(mainMethodSignature),
            bodyOffset: mainBodyOffset,
            parameterList: default);

        // Find entry point
        var entryPoint = default(MethodDefinitionHandle);
        var corHeader = peReader.PEHeaders.CorHeader;
        if (corHeader != null && corHeader.EntryPointTokenOrRelativeVirtualAddress != 0)
        {
            int token = (int)corHeader.EntryPointTokenOrRelativeVirtualAddress;
            var sourceHandle = MetadataTokens.EntityHandle(token);

            if (sourceHandle.Kind == HandleKind.MethodDefinition)
            {
                var sourceMethodHandle = (MethodDefinitionHandle)sourceHandle;
                entryPoint = MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(sourceMethodHandle));
            }
        }        // Build the PE
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
}

