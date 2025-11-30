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
        var rebuilder = new AssemblyRebuilder(verbose, Console.Out);
        rebuilder.RebuildAsILOnly(inputFile, outputFile);
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
