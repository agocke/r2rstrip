using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

/// <summary>
/// Copies metadata and IL from source assembly to target builder.
/// Since we copy all metadata in order, tokens are naturally preserved - no mapping needed.
/// </summary>
class MetadataCopier
{
    private readonly MetadataReader _reader;
    private readonly MetadataBuilder _builder;
    private readonly PEReader _peReader;
    private readonly MethodBodyStreamEncoder _methodBodyEncoder;
    private readonly bool _verbose;

    public MetadataCopier(
        MetadataReader reader,
        MetadataBuilder builder,
        PEReader peReader,
        MethodBodyStreamEncoder methodBodyEncoder,
        bool verbose)
    {
        _reader = reader;
        _builder = builder;
        _peReader = peReader;
        _methodBodyEncoder = methodBodyEncoder;
        _verbose = verbose;
    }

    public void CopyAll()
    {
        CopyStringHeap();
        CopyUserStringHeap();
        CopyModule();
        CopyAssembly();
        CopyAssemblyReferences();
        CopyTypeReferences();
        CopyTypeSpecifications();
        CopyStandaloneSignatures();
        CopyTypeDefinitions();
        CopyFieldDefinitions();
        CopyMethodDefinitions();
        CopyParameters();
        CopyMemberReferences();
        CopyMethodSpecifications();

        if (_verbose)
        {
            Console.WriteLine($"  Copied {_builder.GetRowCount(TableIndex.TypeDef)} types, " +
                $"{_builder.GetRowCount(TableIndex.MethodDef)} methods, " +
                $"{_builder.GetRowCount(TableIndex.Field)} fields");
        }
    }

    private void CopyStringHeap()
    {
        var currentHandle = default(StringHandle);
        int count = 0;

        while (true)
        {
            currentHandle = _reader.GetNextHandle(currentHandle);
            if (currentHandle.IsNil)
                break;

            _builder.GetOrAddString(_reader.GetString(currentHandle));
            count++;
        }

        if (_verbose)
        {
            Console.WriteLine($"  Copied {count} strings from #Strings heap");
        }
    }

    private void CopyUserStringHeap()
    {
        int heapSize = _reader.GetHeapSize(HeapIndex.UserString);
        if (heapSize <= 1) return;

        int heapOffset = _reader.GetHeapMetadataOffset(HeapIndex.UserString);
        var metadataBytes = _peReader.GetMetadata().GetContent();

        int pos = 1; // Skip initial 0x00 byte
        while (pos < heapSize)
        {
            // Read compressed unsigned int (blob length)
            byte b = metadataBytes[heapOffset + pos];
            int blobLength;
            int headerSize;

            if ((b & 0x80) == 0)
            {
                blobLength = b;
                headerSize = 1;
            }
            else if ((b & 0xC0) == 0x80)
            {
                blobLength = ((b & 0x3F) << 8) | metadataBytes[heapOffset + pos + 1];
                headerSize = 2;
            }
            else
            {
                blobLength = ((b & 0x1F) << 24) | (metadataBytes[heapOffset + pos + 1] << 16) |
                             (metadataBytes[heapOffset + pos + 2] << 8) | metadataBytes[heapOffset + pos + 3];
                headerSize = 4;
            }

            if (blobLength > 0)
            {
                var handle = MetadataTokens.UserStringHandle(pos);
                _builder.GetOrAddUserString(_reader.GetUserString(handle));
            }

            pos += headerSize + blobLength;
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

    private void CopyStandaloneSignatures()
    {
        int count = _reader.GetTableRowCount(TableIndex.StandAloneSig);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.StandaloneSignatureHandle(i);
            var sig = _reader.GetStandaloneSignature(handle);
            _builder.AddStandaloneSignature(
                _builder.GetOrAddBlob(_reader.GetBlobBytes(sig.Signature)));
        }
    }

    private void CopyTypeDefinitions()
    {
        int nextFieldRow = 1;
        int nextMethodRow = 1;

        foreach (var handle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(handle);

            _builder.AddTypeDefinition(
                attributes: typeDef.Attributes,
                @namespace: _builder.GetOrAddString(_reader.GetString(typeDef.Namespace)),
                name: _builder.GetOrAddString(_reader.GetString(typeDef.Name)),
                baseType: typeDef.BaseType,
                fieldList: MetadataTokens.FieldDefinitionHandle(nextFieldRow),
                methodList: MetadataTokens.MethodDefinitionHandle(nextMethodRow));

            nextFieldRow += typeDef.GetFields().Count;
            nextMethodRow += typeDef.GetMethods().Count;
        }
    }

    private void CopyFieldDefinitions()
    {
        foreach (var handle in _reader.FieldDefinitions)
        {
            var fieldDef = _reader.GetFieldDefinition(handle);
            _builder.AddFieldDefinition(
                attributes: fieldDef.Attributes,
                name: _builder.GetOrAddString(_reader.GetString(fieldDef.Name)),
                signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(fieldDef.Signature)));
        }
    }

    private void CopyMethodDefinitions()
    {
        int nextParamRow = 1;

        foreach (var handle in _reader.MethodDefinitions)
        {
            var methodDef = _reader.GetMethodDefinition(handle);
            int bodyOffset = CopyMethodBody(methodDef);

            _builder.AddMethodDefinition(
                attributes: methodDef.Attributes,
                implAttributes: methodDef.ImplAttributes,
                name: _builder.GetOrAddString(_reader.GetString(methodDef.Name)),
                signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(methodDef.Signature)),
                bodyOffset: bodyOffset,
                parameterList: MetadataTokens.ParameterHandle(nextParamRow));

            nextParamRow += methodDef.GetParameters().Count;
        }
    }

    private int CopyMethodBody(MethodDefinition methodDef)
    {
        int rva = methodDef.RelativeVirtualAddress;
        if (rva == 0)
            return -1;

        var body = _peReader.GetMethodBody(rva);
        var ilBytes = body.GetILBytes();
        if (ilBytes == null || ilBytes.Length == 0)
            return -1;

        var localSig = body.LocalSignature.IsNil ? default : body.LocalSignature;
        var attributes = body.LocalVariablesInitialized
            ? MethodBodyAttributes.InitLocals
            : MethodBodyAttributes.None;

        var methodBody = _methodBodyEncoder.AddMethodBody(
            codeSize: ilBytes.Length,
            maxStack: body.MaxStack,
            exceptionRegionCount: body.ExceptionRegions.Length,
            hasSmallExceptionRegions: false,
            localVariablesSignature: localSig,
            attributes: attributes,
            hasDynamicStackAllocation: false);

        new BlobWriter(methodBody.Instructions).WriteBytes(ilBytes);

        foreach (var region in body.ExceptionRegions)
        {
            switch (region.Kind)
            {
                case ExceptionRegionKind.Catch:
                    methodBody.ExceptionRegions.AddCatch(
                        region.TryOffset, region.TryLength,
                        region.HandlerOffset, region.HandlerLength,
                        region.CatchType);
                    break;
                case ExceptionRegionKind.Filter:
                    methodBody.ExceptionRegions.AddFilter(
                        region.TryOffset, region.TryLength,
                        region.HandlerOffset, region.HandlerLength,
                        region.FilterOffset);
                    break;
                case ExceptionRegionKind.Finally:
                    methodBody.ExceptionRegions.AddFinally(
                        region.TryOffset, region.TryLength,
                        region.HandlerOffset, region.HandlerLength);
                    break;
                case ExceptionRegionKind.Fault:
                    methodBody.ExceptionRegions.AddFault(
                        region.TryOffset, region.TryLength,
                        region.HandlerOffset, region.HandlerLength);
                    break;
            }
        }

        return methodBody.Offset;
    }

    private void CopyParameters()
    {
        foreach (var methodHandle in _reader.MethodDefinitions)
        {
            var methodDef = _reader.GetMethodDefinition(methodHandle);
            foreach (var paramHandle in methodDef.GetParameters())
            {
                var param = _reader.GetParameter(paramHandle);
                _builder.AddParameter(
                    attributes: param.Attributes,
                    name: param.Name.IsNil
                        ? default
                        : _builder.GetOrAddString(_reader.GetString(param.Name)),
                    sequenceNumber: param.SequenceNumber);
            }
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

    private void CopyTypeSpecifications()
    {
        int count = _reader.GetTableRowCount(TableIndex.TypeSpec);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.TypeSpecificationHandle(i);
            var typeSpec = _reader.GetTypeSpecification(handle);
            _builder.AddTypeSpecification(
                _builder.GetOrAddBlob(_reader.GetBlobBytes(typeSpec.Signature)));
        }
    }

    private void CopyMethodSpecifications()
    {
        int count = _reader.GetTableRowCount(TableIndex.MethodSpec);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.MethodSpecificationHandle(i);
            var methodSpec = _reader.GetMethodSpecification(handle);
            _builder.AddMethodSpecification(
                method: methodSpec.Method,
                instantiation: _builder.GetOrAddBlob(_reader.GetBlobBytes(methodSpec.Signature)));
        }
    }
}
