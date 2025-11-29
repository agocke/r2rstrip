using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
/// <summary>
/// Copies metadata from source assembly to target builder.
/// Since we copy all metadata in order, tokens are naturally preserved - no mapping needed.
/// </summary>
class MetadataCopier
{
    private readonly MetadataReader _reader;
    private readonly MetadataBuilder _builder;
    private readonly bool _verbose;

    public MetadataCopier(
        MetadataReader reader,
        MetadataBuilder builder,
        bool verbose)
    {
        _reader = reader;
        _builder = builder;
        _verbose = verbose;
    }

    public void CopyAll()
    {
        // Copy in dependency order
        CopyStringHeap();  // Pre-populate string heap to preserve all strings
        CopyModule();
        CopyAssembly();
        CopyAssemblyReferences();
        CopyTypeReferences();
        CopyTypeDefinitions();
        CopyMemberReferences();

        if (_verbose)
        {
            Console.WriteLine($"  Copied {_builder.GetRowCount(TableIndex.TypeDef)} types");
        }
    }

    private void CopyStringHeap()
    {
        // Enumerate all strings from the source #Strings heap and add them to the builder
        // This ensures the string table is preserved even if we don't copy all metadata that references them
        var currentHandle = default(StringHandle);
        int count = 0;

        while (true)
        {
            currentHandle = _reader.GetNextHandle(currentHandle);
            if (currentHandle.IsNil)
                break;

            var str = _reader.GetString(currentHandle);
            _builder.GetOrAddString(str);
            count++;
        }

        if (_verbose)
        {
            Console.WriteLine($"  Copied {count} strings from #Strings heap");
        }
    }

    private void CopyModule()
    {
        var moduleDef = _reader.GetModuleDefinition();
        _builder.AddModule(
            generation: moduleDef.Generation,
            moduleName: _builder.GetOrAddString(_reader.GetString(moduleDef.Name)),
            mvid: _builder.GetOrAddGuid(_reader.GetGuid(moduleDef.Mvid)),
            encId: default,
            encBaseId: default);
    }

    private void CopyAssembly()
    {
        var assemblyDef = _reader.GetAssemblyDefinition();

        var publicKeyBlob = default(BlobHandle);
        if (!assemblyDef.PublicKey.IsNil)
        {
            publicKeyBlob = _builder.GetOrAddBlob(_reader.GetBlobBytes(assemblyDef.PublicKey));
        }

        _builder.AddAssembly(
            name: _builder.GetOrAddString(_reader.GetString(assemblyDef.Name)),
            version: assemblyDef.Version,
            culture: _builder.GetOrAddString(_reader.GetString(assemblyDef.Culture)),
            publicKey: publicKeyBlob,
            flags: assemblyDef.Flags,
            hashAlgorithm: assemblyDef.HashAlgorithm);
    }

    private void CopyAssemblyReferences()
    {
        foreach (var handle in _reader.AssemblyReferences)
        {
            var assemblyRef = _reader.GetAssemblyReference(handle);

            var publicKeyOrTokenBlob = default(BlobHandle);
            if (!assemblyRef.PublicKeyOrToken.IsNil)
            {
                publicKeyOrTokenBlob = _builder.GetOrAddBlob(_reader.GetBlobBytes(assemblyRef.PublicKeyOrToken));
            }

            _builder.AddAssemblyReference(
                name: _builder.GetOrAddString(_reader.GetString(assemblyRef.Name)),
                version: assemblyRef.Version,
                culture: _builder.GetOrAddString(_reader.GetString(assemblyRef.Culture)),
                publicKeyOrToken: publicKeyOrTokenBlob,
                flags: assemblyRef.Flags,
                hashValue: default);
        }
    }

    private void CopyTypeReferences()
    {
        foreach (var handle in _reader.TypeReferences)
        {
            var typeRef = _reader.GetTypeReference(handle);

            _builder.AddTypeReference(
                resolutionScope: typeRef.ResolutionScope,
                @namespace: _builder.GetOrAddString(_reader.GetString(typeRef.Namespace)),
                name: _builder.GetOrAddString(_reader.GetString(typeRef.Name)));
        }
    }

    private void CopyTypeDefinitions()
    {
        foreach (var handle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(handle);

            _builder.AddTypeDefinition(
                attributes: typeDef.Attributes,
                @namespace: _builder.GetOrAddString(_reader.GetString(typeDef.Namespace)),
                name: _builder.GetOrAddString(_reader.GetString(typeDef.Name)),
                baseType: typeDef.BaseType,
                fieldList: MetadataTokens.FieldDefinitionHandle(_builder.GetRowCount(TableIndex.Field) + 1),
                methodList: MetadataTokens.MethodDefinitionHandle(_builder.GetRowCount(TableIndex.MethodDef) + 1));
        }
    }

    private void CopyMemberReferences()
    {
        foreach (var handle in _reader.MemberReferences)
        {
            var memberRef = _reader.GetMemberReference(handle);

            _builder.AddMemberReference(
                parent: memberRef.Parent,
                name: _builder.GetOrAddString(_reader.GetString(memberRef.Name)),
                signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(memberRef.Signature)));
        }
    }
}
