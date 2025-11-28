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
    private readonly MethodBodyStreamEncoder _methodBodyEncoder;
    private readonly PEReader _peReader;
    private readonly bool _verbose;
    
    public MetadataCopier(
        MetadataReader reader, 
        MetadataBuilder builder,
        MethodBodyStreamEncoder methodBodyEncoder,
        PEReader peReader,
        bool verbose)
    {
        _reader = reader;
        _builder = builder;
        _methodBodyEncoder = methodBodyEncoder;
        _peReader = peReader;
        _verbose = verbose;
    }
    
    public void CopyAll()
    {
        // Copy in dependency order
        CopyModule();
        CopyAssembly();
        CopyAssemblyReferences();
        CopyTypeReferences();
        CopyTypeDefinitions();
        CopyMemberReferences();
        
        if (_verbose)
        {
            Console.WriteLine($"  Copied {_builder.GetRowCount(TableIndex.TypeDef)} types");
            Console.WriteLine($"  Copied {_builder.GetRowCount(TableIndex.MethodDef)} methods");
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
            
            // Copy fields
            foreach (var fieldHandle in typeDef.GetFields())
            {
                CopyField(fieldHandle);
            }
            
            // Copy methods
            foreach (var methodHandle in typeDef.GetMethods())
            {
                CopyMethod(methodHandle);
            }
        }
    }
    
    private void CopyField(FieldDefinitionHandle handle)
    {
        var field = _reader.GetFieldDefinition(handle);
        
        _builder.AddFieldDefinition(
            attributes: field.Attributes,
            name: _builder.GetOrAddString(_reader.GetString(field.Name)),
            signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(field.Signature)));
    }
    
    private void CopyMethod(MethodDefinitionHandle handle)
    {
        var method = _reader.GetMethodDefinition(handle);
        
        // Get IL body if it exists
        var bodyOffset = -1;
        if (method.RelativeVirtualAddress != 0)
        {
            try
            {
                // Get the raw method body bytes (including header)
                var methodBody = _peReader.GetMethodBody(method.RelativeVirtualAddress);
                byte[] fullMethodBody = methodBody.GetILContent().ToArray();
                
                if (fullMethodBody != null && fullMethodBody.Length > 0)
                {
                    // Record the offset in our IL stream
                    bodyOffset = _methodBodyEncoder.Builder.Count;
                    
                    // Write the complete method body (header + IL + exception handling)
                    _methodBodyEncoder.Builder.WriteBytes(fullMethodBody);
                    
                    // Align to 4-byte boundary
                    while (_methodBodyEncoder.Builder.Count % 4 != 0)
                    {
                        _methodBodyEncoder.Builder.WriteByte(0);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_verbose)
                {
                    var methodName = _reader.GetString(method.Name);
                    Console.WriteLine($"    Warning: Could not read IL for method '{methodName}': {ex.Message}");
                }
            }
        }
        
        _builder.AddMethodDefinition(
            attributes: method.Attributes,
            implAttributes: method.ImplAttributes,
            name: _builder.GetOrAddString(_reader.GetString(method.Name)),
            signature: _builder.GetOrAddBlob(_reader.GetBlobBytes(method.Signature)),
            bodyOffset: bodyOffset,
            parameterList: MetadataTokens.ParameterHandle(_builder.GetRowCount(TableIndex.Param) + 1));
        
        // Copy parameters
        foreach (var paramHandle in method.GetParameters())
        {
            CopyParameter(paramHandle);
        }
    }
    
    private void CopyParameter(ParameterHandle handle)
    {
        var param = _reader.GetParameter(handle);
        
        _builder.AddParameter(
            attributes: param.Attributes,
            sequenceNumber: param.SequenceNumber,
            name: _builder.GetOrAddString(_reader.GetString(param.Name)));
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

