using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace R2RStrip;

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
    private readonly BlobBuilder _mappedFieldData;
    private readonly BlobBuilder _managedResources;
    private readonly bool _verbose;

    public MetadataCopier(
        MetadataReader reader,
        MetadataBuilder builder,
        PEReader peReader,
        MethodBodyStreamEncoder methodBodyEncoder,
        BlobBuilder mappedFieldData,
        BlobBuilder managedResources,
        bool verbose)
    {
        _reader = reader;
        _builder = builder;
        _peReader = peReader;
        _methodBodyEncoder = methodBodyEncoder;
        _mappedFieldData = mappedFieldData;
        _managedResources = managedResources;
        _verbose = verbose;
    }

    public void CopyAll()
    {
        CopyStringHeap();
        CopyUserStringHeap();
        CopyModule();
        CopyAssembly();
        CopyAssemblyReferences();
        CopyModuleReferences();
        CopyTypeReferences();
        CopyTypeSpecifications();
        CopyStandaloneSignatures();
        CopyTypeDefinitions();
        CopyFieldDefinitions();
        CopyMethodDefinitions();
        CopyParameters();
        CopyInterfaceImplementations();
        CopyMemberReferences();
        CopyMethodSpecifications();
        CopyConstants();
        CopyCustomAttributes();
        CopyFieldMarshals();
        CopyDeclSecurities();
        CopyClassLayouts();
        CopyFieldLayouts();
        CopyFieldRvas();
        CopyImplMaps();
        CopyPropertyMaps();
        CopyProperties();
        CopyEventMaps();
        CopyEvents();
        CopyMethodSemantics();
        CopyMethodImpls();
        CopyGenericParameters();
        CopyGenericParamConstraints();
        CopyNestedClasses();
        CopyExportedTypes();
        CopyManifestResources();

        if (_verbose)
        {
            Console.WriteLine($"  Copied {_builder.GetRowCount(TableIndex.TypeDef)} types, " +
                $"{_builder.GetRowCount(TableIndex.MethodDef)} methods, " +
                $"{_builder.GetRowCount(TableIndex.Field)} fields, " +
                $"{_builder.GetRowCount(TableIndex.GenericParam)} generic params");
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

    private void CopyModuleReferences()
    {
        int count = _reader.GetTableRowCount(TableIndex.ModuleRef);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.ModuleReferenceHandle(i);
            var moduleRef = _reader.GetModuleReference(handle);
            _builder.AddModuleReference(
                _builder.GetOrAddString(_reader.GetString(moduleRef.Name)));
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

    private void CopyInterfaceImplementations()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            foreach (var ifaceHandle in typeDef.GetInterfaceImplementations())
            {
                var iface = _reader.GetInterfaceImplementation(ifaceHandle);
                _builder.AddInterfaceImplementation(
                    type: typeHandle,
                    implementedInterface: iface.Interface);
            }
        }
    }

    private void CopyGenericParameters()
    {
        int count = _reader.GetTableRowCount(TableIndex.GenericParam);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.GenericParameterHandle(i);
            var gp = _reader.GetGenericParameter(handle);
            _builder.AddGenericParameter(
                parent: gp.Parent,
                attributes: gp.Attributes,
                name: _builder.GetOrAddString(_reader.GetString(gp.Name)),
                index: gp.Index);
        }
    }

    private void CopyGenericParamConstraints()
    {
        int count = _reader.GetTableRowCount(TableIndex.GenericParamConstraint);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.GenericParameterConstraintHandle(i);
            var constraint = _reader.GetGenericParameterConstraint(handle);
            _builder.AddGenericParameterConstraint(
                genericParameter: constraint.Parameter,
                constraint: constraint.Type);
        }
    }

    private void CopyNestedClasses()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            if (typeDef.IsNested)
            {
                _builder.AddNestedType(
                    type: typeHandle,
                    enclosingType: typeDef.GetDeclaringType());
            }
        }
    }

    private void CopyConstants()
    {
        int count = _reader.GetTableRowCount(TableIndex.Constant);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.ConstantHandle(i);
            var constant = _reader.GetConstant(handle);
            var blob = _reader.GetBlobBytes(constant.Value);
            object? value = DecodeConstant(constant.TypeCode, blob);
            _builder.AddConstant(
                parent: constant.Parent,
                value: value);
        }
    }

    private static object? DecodeConstant(ConstantTypeCode typeCode, byte[] blob)
    {
        return typeCode switch
        {
            ConstantTypeCode.Boolean => blob.Length >= 1 && blob[0] != 0,
            ConstantTypeCode.Char => BitConverter.ToChar(blob, 0),
            ConstantTypeCode.SByte => (sbyte)blob[0],
            ConstantTypeCode.Byte => blob[0],
            ConstantTypeCode.Int16 => BitConverter.ToInt16(blob, 0),
            ConstantTypeCode.UInt16 => BitConverter.ToUInt16(blob, 0),
            ConstantTypeCode.Int32 => BitConverter.ToInt32(blob, 0),
            ConstantTypeCode.UInt32 => BitConverter.ToUInt32(blob, 0),
            ConstantTypeCode.Int64 => BitConverter.ToInt64(blob, 0),
            ConstantTypeCode.UInt64 => BitConverter.ToUInt64(blob, 0),
            ConstantTypeCode.Single => BitConverter.ToSingle(blob, 0),
            ConstantTypeCode.Double => BitConverter.ToDouble(blob, 0),
            ConstantTypeCode.String => blob.Length > 0 ? System.Text.Encoding.Unicode.GetString(blob) : null,
            ConstantTypeCode.NullReference => null,
            _ => null,
        };
    }

    private void CopyCustomAttributes()
    {
        foreach (var handle in _reader.CustomAttributes)
        {
            var attr = _reader.GetCustomAttribute(handle);
            _builder.AddCustomAttribute(
                parent: attr.Parent,
                constructor: attr.Constructor,
                value: _builder.GetOrAddBlob(_reader.GetBlobBytes(attr.Value)));
        }
    }

    private void CopyFieldMarshals()
    {
        foreach (var handle in _reader.FieldDefinitions)
        {
            var field = _reader.GetFieldDefinition(handle);
            var marshalInfo = field.GetMarshallingDescriptor();
            if (!marshalInfo.IsNil)
            {
                _builder.AddMarshallingDescriptor(
                    parent: handle,
                    descriptor: _builder.GetOrAddBlob(_reader.GetBlobBytes(marshalInfo)));
            }
        }
        foreach (var methodHandle in _reader.MethodDefinitions)
        {
            var method = _reader.GetMethodDefinition(methodHandle);
            foreach (var paramHandle in method.GetParameters())
            {
                var param = _reader.GetParameter(paramHandle);
                var marshalInfo = param.GetMarshallingDescriptor();
                if (!marshalInfo.IsNil)
                {
                    _builder.AddMarshallingDescriptor(
                        parent: paramHandle,
                        descriptor: _builder.GetOrAddBlob(_reader.GetBlobBytes(marshalInfo)));
                }
            }
        }
    }

    private void CopyDeclSecurities()
    {
        int count = _reader.GetTableRowCount(TableIndex.DeclSecurity);
        for (int i = 1; i <= count; i++)
        {
            var handle = MetadataTokens.DeclarativeSecurityAttributeHandle(i);
            var security = _reader.GetDeclarativeSecurityAttribute(handle);
            _builder.AddDeclarativeSecurityAttribute(
                parent: security.Parent,
                action: security.Action,
                permissionSet: _builder.GetOrAddBlob(_reader.GetBlobBytes(security.PermissionSet)));
        }
    }

    private void CopyClassLayouts()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            var layout = typeDef.GetLayout();
            if (!layout.IsDefault)
            {
                _builder.AddTypeLayout(
                    type: typeHandle,
                    packingSize: (ushort)layout.PackingSize,
                    size: (uint)layout.Size);
            }
        }
    }

    private void CopyFieldLayouts()
    {
        foreach (var handle in _reader.FieldDefinitions)
        {
            var field = _reader.GetFieldDefinition(handle);
            int offset = field.GetOffset();
            if (offset >= 0)
            {
                _builder.AddFieldLayout(
                    field: handle,
                    offset: offset);
            }
        }
    }

    private void CopyFieldRvas()
    {
        foreach (var handle in _reader.FieldDefinitions)
        {
            var field = _reader.GetFieldDefinition(handle);
            int rva = field.GetRelativeVirtualAddress();
            if (rva != 0)
            {
                // Determine the mapped field data size from the field signature
                int size = GetMappedFieldDataSize(field);
                if (size > 0)
                {
                    var sectionData = _peReader.GetSectionData(rva);
                    var content = sectionData.GetContent(0, size);

                    int offset = _mappedFieldData.Count;
                    _mappedFieldData.WriteBytes(content);

                    _builder.AddFieldRelativeVirtualAddress(
                        field: handle,
                        offset: offset);
                }
            }
        }
    }

    private int GetMappedFieldDataSize(FieldDefinition field)
    {
        var sigBytes = _reader.GetBlobBytes(field.Signature);
        // Field signature: FIELD(0x06) <type>
        // For mapped fields, the type is usually a value type with explicit layout.
        // We can estimate size from the ClassLayout of the field's type.
        // But a simpler approach: if the sig points to a valuetype, look up its ClassLayout size.
        if (sigBytes.Length >= 2 && sigBytes[0] == 0x06)
        {
            // Check if it's a valuetype reference (VALUETYPE = 0x11)
            int pos = 1;
            byte typeTag = sigBytes[pos];

            if (typeTag == 0x11) // ELEMENT_TYPE_VALUETYPE
            {
                // Decode compressed token
                pos++;
                int codedIndex = DecodeCompressedUInt(sigBytes, ref pos);
                int tableIndex = codedIndex & 0x03;
                int rowIndex = codedIndex >> 2;

                TypeDefinitionHandle typeHandle;
                if (tableIndex == 0) // TypeDef
                    typeHandle = MetadataTokens.TypeDefinitionHandle(rowIndex);
                else
                    return 0; // TypeRef — can't resolve size locally

                var typeDef = _reader.GetTypeDefinition(typeHandle);
                var layout = typeDef.GetLayout();
                if (!layout.IsDefault && layout.Size > 0)
                    return layout.Size;
            }
            else
            {
                // Primitive type sizes
                return typeTag switch
                {
                    0x02 => 1, // Boolean
                    0x03 => 2, // Char
                    0x04 => 1, // I1
                    0x05 => 1, // U1
                    0x06 => 2, // I2
                    0x07 => 2, // U2
                    0x08 => 4, // I4
                    0x09 => 4, // U4
                    0x0A => 8, // I8
                    0x0B => 8, // U8
                    0x0C => 4, // R4
                    0x0D => 8, // R8
                    _ => 0,
                };
            }
        }
        return 0;
    }

    private static int DecodeCompressedUInt(byte[] data, ref int pos)
    {
        byte b = data[pos];
        if ((b & 0x80) == 0)
        {
            pos++;
            return b;
        }
        else if ((b & 0xC0) == 0x80)
        {
            int val = ((b & 0x3F) << 8) | data[pos + 1];
            pos += 2;
            return val;
        }
        else
        {
            int val = ((b & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return val;
        }
    }

    private void CopyImplMaps()
    {
        foreach (var methodHandle in _reader.MethodDefinitions)
        {
            var method = _reader.GetMethodDefinition(methodHandle);
            var import = method.GetImport();
            if (!import.Module.IsNil)
            {
                _builder.AddMethodImport(
                    method: methodHandle,
                    attributes: import.Attributes,
                    name: _builder.GetOrAddString(_reader.GetString(import.Name)),
                    module: import.Module);
            }
        }
    }

    private void CopyPropertyMaps()
    {
        int nextPropertyRow = 1;
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            var properties = typeDef.GetProperties();
            if (properties.Count > 0)
            {
                _builder.AddPropertyMap(
                    declaringType: typeHandle,
                    propertyList: MetadataTokens.PropertyDefinitionHandle(nextPropertyRow));
                nextPropertyRow += properties.Count;
            }
        }
    }

    private void CopyProperties()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            foreach (var propHandle in typeDef.GetProperties())
            {
                var prop = _reader.GetPropertyDefinition(propHandle);
                _builder.AddProperty(
                    attributes: prop.Attributes,
                    name: _builder.GetOrAddString(_reader.GetString(prop.Name)),
                    signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(prop.Signature)));
            }
        }
    }

    private void CopyEventMaps()
    {
        int nextEventRow = 1;
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            var events = typeDef.GetEvents();
            if (events.Count > 0)
            {
                _builder.AddEventMap(
                    declaringType: typeHandle,
                    eventList: MetadataTokens.EventDefinitionHandle(nextEventRow));
                nextEventRow += events.Count;
            }
        }
    }

    private void CopyEvents()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            foreach (var eventHandle in typeDef.GetEvents())
            {
                var evt = _reader.GetEventDefinition(eventHandle);
                _builder.AddEvent(
                    attributes: evt.Attributes,
                    name: _builder.GetOrAddString(_reader.GetString(evt.Name)),
                    type: evt.Type);
            }
        }
    }

    private void CopyMethodSemantics()
    {
        // Property accessors
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            foreach (var propHandle in typeDef.GetProperties())
            {
                var prop = _reader.GetPropertyDefinition(propHandle);
                var accessors = prop.GetAccessors();
                if (!accessors.Getter.IsNil)
                    _builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Getter, accessors.Getter);
                if (!accessors.Setter.IsNil)
                    _builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Setter, accessors.Setter);
                foreach (var other in accessors.Others)
                    _builder.AddMethodSemantics(propHandle, MethodSemanticsAttributes.Other, other);
            }
            foreach (var eventHandle in typeDef.GetEvents())
            {
                var evt = _reader.GetEventDefinition(eventHandle);
                var accessors = evt.GetAccessors();
                if (!accessors.Adder.IsNil)
                    _builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Adder, accessors.Adder);
                if (!accessors.Remover.IsNil)
                    _builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Remover, accessors.Remover);
                if (!accessors.Raiser.IsNil)
                    _builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Raiser, accessors.Raiser);
                foreach (var other in accessors.Others)
                    _builder.AddMethodSemantics(eventHandle, MethodSemanticsAttributes.Other, other);
            }
        }
    }

    private void CopyMethodImpls()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var typeDef = _reader.GetTypeDefinition(typeHandle);
            foreach (var implHandle in typeDef.GetMethodImplementations())
            {
                var impl = _reader.GetMethodImplementation(implHandle);
                _builder.AddMethodImplementation(
                    type: typeHandle,
                    methodBody: impl.MethodBody,
                    methodDeclaration: impl.MethodDeclaration);
            }
        }
    }

    private void CopyExportedTypes()
    {
        foreach (var handle in _reader.ExportedTypes)
        {
            var exported = _reader.GetExportedType(handle);
            _builder.AddExportedType(
                attributes: exported.Attributes,
                @namespace: _builder.GetOrAddString(_reader.GetString(exported.Namespace)),
                name: _builder.GetOrAddString(_reader.GetString(exported.Name)),
                implementation: exported.Implementation,
                typeDefinitionId: exported.GetTypeDefinitionId());
        }
    }

    private void CopyManifestResources()
    {
        foreach (var handle in _reader.ManifestResources)
        {
            var resource = _reader.GetManifestResource(handle);

            uint offset = (uint)resource.Offset;
            if (resource.Implementation.IsNil)
            {
                // Embedded resource — copy its data into _managedResources
                var resourceDir = _peReader.PEHeaders.CorHeader!.ResourcesDirectory;
                var sectionData = _peReader.GetSectionData(resourceDir.RelativeVirtualAddress);
                var content = sectionData.GetContent();

                // Read the 4-byte length prefix at the resource offset
                int dataOffset = (int)resource.Offset;
                int length = content[dataOffset]
                    | (content[dataOffset + 1] << 8)
                    | (content[dataOffset + 2] << 16)
                    | (content[dataOffset + 3] << 24);

                // Write at the current position in the managed resources blob
                offset = (uint)_managedResources.Count;
                _managedResources.WriteInt32(length);
                _managedResources.WriteBytes(content.AsSpan(dataOffset + 4, length).ToArray());
            }

            _builder.AddManifestResource(
                attributes: resource.Attributes,
                name: _builder.GetOrAddString(_reader.GetString(resource.Name)),
                implementation: resource.Implementation,
                offset: offset);
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
