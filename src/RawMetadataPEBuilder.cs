using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

/// <summary>
/// Custom PE builder that writes metadata and IL streams directly without using MetadataBuilder.
/// This preserves the exact heap structure from the source R2R assembly.
/// </summary>
public class RawMetadataPEBuilder : PEBuilder
{
    private const string TextSectionName = ".text";
    private const int SectionAlignment = 0x2000;
    private const int FileAlignment = 0x200;

    private readonly PEDirectoriesBuilder _peDirectoriesBuilder;
    private readonly CorFlags _corFlags;
    private readonly MethodDefinitionHandle _entryPoint;

    public RawMetadataPEBuilder(
        PEHeaderBuilder header,
        CorFlags corFlags = CorFlags.ILOnly,
        MethodDefinitionHandle entryPoint = default)
        : base(header, deterministicIdProvider: null)
    {
        _peDirectoriesBuilder = new PEDirectoriesBuilder();
        _corFlags = corFlags;
        _entryPoint = entryPoint;
    }

    protected override ImmutableArray<Section> CreateSections()
    {
        // For now, just create an empty .text section
        // We'll expand this as we implement more functionality
        var sections = ImmutableArray.CreateBuilder<Section>();
        
        sections.Add(new Section(TextSectionName, SectionCharacteristics.ContainsCode |
                                                   SectionCharacteristics.MemExecute |
                                                   SectionCharacteristics.MemRead));
        
        return sections.ToImmutable();
    }

    protected override BlobBuilder SerializeSection(string name, SectionLocation location)
    {
        if (name == TextSectionName)
        {
            return SerializeTextSection(location);
        }

        throw new ArgumentException($"Unknown section: {name}", nameof(name));
    }

    private BlobBuilder SerializeTextSection(SectionLocation location)
    {
        var builder = new BlobBuilder();
        
        // Build the .text section with:
        // 1. Import Address Table (IAT)
        // 2. CLI Metadata
        // 3. COR20 Header
        
        // Write minimal IAT (8 bytes)
        var iatBuilder = new BlobBuilder();
        iatBuilder.WriteUInt32(0); // mscoree.dll!_CorExeMain or _CorDllMain
        iatBuilder.WriteUInt32(0); // null terminator
        
        // Write minimal CLI metadata
        var metadataBuilder = new BlobBuilder();
        int metadataSize = WriteMinimalMetadata(metadataBuilder);
        
        // Write COR20 header (72 bytes)
        var corHeaderBuilder = new BlobBuilder();
        
        // Assemble the section - calculate offsets
        builder.LinkSuffix(iatBuilder);
        builder.Align(4);
        
        int metadataOffset = builder.Count;
        builder.LinkSuffix(metadataBuilder);
        builder.Align(4);
        
        int corHeaderOffset = builder.Count;
        WriteCorHeader(corHeaderBuilder, location, metadataOffset, metadataSize);
        builder.LinkSuffix(corHeaderBuilder);
        builder.Align(4);
        
        // Set up directories
        _peDirectoriesBuilder.CorHeaderTable = new DirectoryEntry(
            location.RelativeVirtualAddress + corHeaderOffset,
            corHeaderBuilder.Count);
        
        return builder;
    }

    private int WriteMinimalMetadata(BlobBuilder builder)
    {
        int startOffset = builder.Count;
        
        // Write minimal CLI metadata header (ECMA-335 §II.24.2.1)
        // Signature
        builder.WriteUInt32(0x424A5342); // "BSJB" signature
        
        // Version
        builder.WriteUInt16(1); // Major version
        builder.WriteUInt16(1); // Minor version
        
        // Reserved
        builder.WriteUInt32(0);
        
        // Version string length (aligned to 4 bytes)
        string versionString = "v4.0.30319";
        int versionLength = versionString.Length;
        int versionLengthAligned = (versionLength + 3) & ~3; // Round up to multiple of 4
        builder.WriteUInt32((uint)versionLengthAligned);
        
        // Version string
        foreach (char c in versionString)
        {
            builder.WriteByte((byte)c);
        }
        // Padding to align to 4 bytes
        for (int i = versionLength; i < versionLengthAligned; i++)
        {
            builder.WriteByte(0);
        }
        
        // Flags
        builder.WriteUInt16(0);
        
        // Number of streams (we'll write 5: #~, #Strings, #US, #GUID, #Blob)
        builder.WriteUInt16(5);
        
        // Calculate stream offsets (relative to start of metadata)
        int headerSize = 4 + 2 + 2 + 4 + 4 + versionLengthAligned + 2 + 2; // What we've written so far
        
        // Stream headers: each is 8 bytes (offset + size) + aligned name
        int streamHeadersSize = 
            (8 + 4) +   // #~ (name is 4 bytes including null and padding)
            (8 + 12) +  // #Strings (12 bytes for name)
            (8 + 4) +   // #US (4 bytes)
            (8 + 8) +   // #GUID (8 bytes)
            (8 + 8);    // #Blob (8 bytes)
        
        int streamsOffset = headerSize + streamHeadersSize;
        
        // Stream 1: #~ (metadata tables) - minimal empty tables
        int tablesStreamSize = WriteMinimalTablesStream(out var tablesStreamData);
        WriteStreamHeader(builder, streamsOffset, tablesStreamSize, "#~");
        
        // Stream 2: #Strings - just a single null byte
        int stringsStreamSize = 1;
        WriteStreamHeader(builder, streamsOffset + tablesStreamSize, stringsStreamSize, "#Strings");
        
        // Stream 3: #US (user strings) - just a single null byte
        int usStreamSize = 1;
        WriteStreamHeader(builder, streamsOffset + tablesStreamSize + stringsStreamSize, usStreamSize, "#US");
        
        // Stream 4: #GUID - empty (size 0)
        int guidStreamSize = 0;
        WriteStreamHeader(builder, streamsOffset + tablesStreamSize + stringsStreamSize + usStreamSize, guidStreamSize, "#GUID");
        
        // Stream 5: #Blob - just a single null byte
        int blobStreamSize = 1;
        WriteStreamHeader(builder, streamsOffset + tablesStreamSize + stringsStreamSize + usStreamSize + guidStreamSize, blobStreamSize, "#Blob");
        
        // Now write the actual stream data
        builder.WriteBytes(tablesStreamData);
        builder.WriteByte(0); // #Strings
        builder.WriteByte(0); // #US
        // #GUID is empty
        builder.WriteByte(0); // #Blob
        
        return builder.Count - startOffset;
    }

    private void WriteStreamHeader(BlobBuilder builder, int offset, int size, string name)
    {
        builder.WriteUInt32((uint)offset);
        builder.WriteUInt32((uint)size);
        
        // Write name, null-terminated and padded to 4-byte boundary
        foreach (char c in name)
        {
            builder.WriteByte((byte)c);
        }
        builder.WriteByte(0); // null terminator
        
        // Pad to 4-byte boundary
        int nameLength = name.Length + 1;
        int padding = (4 - (nameLength % 4)) % 4;
        for (int i = 0; i < padding; i++)
        {
            builder.WriteByte(0);
        }
    }

    private int WriteMinimalTablesStream(out byte[] data)
    {
        var builder = new BlobBuilder();
        
        // Metadata tables stream header (ECMA-335 §II.24.2.6)
        builder.WriteUInt32(0); // Reserved
        builder.WriteByte(2);   // MajorVersion
        builder.WriteByte(0);   // MinorVersion
        builder.WriteByte(0);   // HeapSizes (no large heaps)
        builder.WriteByte(1);   // Reserved
        
        // Valid - bitmask of which tables are present (we need Module table at minimum)
        // Module = 0x00, TypeRef = 0x01, TypeDef = 0x02, etc.
        // For minimal valid metadata, we need: Module (0x00) and Assembly (0x20)
        long valid = (1L << 0x00) | (1L << 0x20); // Module and Assembly tables
        builder.WriteUInt64((ulong)valid);
        
        // Sorted - bitmask of which tables are sorted
        builder.WriteUInt64(0);
        
        // Row counts for each present table
        builder.WriteUInt32(1); // Module table: 1 row
        builder.WriteUInt32(1); // Assembly table: 1 row
        
        // Module table (1 row) - ECMA-335 §II.22.30
        // Generation (2 bytes), Name (StringIndex), Mvid (GuidIndex), EncId (GuidIndex), EncBaseId (GuidIndex)
        builder.WriteUInt16(0); // Generation
        builder.WriteUInt16(0); // Name (index into #Strings, 0 = empty)
        builder.WriteUInt16(0); // Mvid (index into #GUID, 0 = empty)
        builder.WriteUInt16(0); // EncId
        builder.WriteUInt16(0); // EncBaseId
        
        // Assembly table (1 row) - ECMA-335 §II.22.2
        // HashAlgId (4), MajorVersion (2), MinorVersion (2), BuildNumber (2), RevisionNumber (2),
        // Flags (4), PublicKey (BlobIndex), Name (StringIndex), Culture (StringIndex)
        builder.WriteUInt32(0x8004); // HashAlgId (SHA1)
        builder.WriteUInt16(1);      // MajorVersion
        builder.WriteUInt16(0);      // MinorVersion
        builder.WriteUInt16(0);      // BuildNumber
        builder.WriteUInt16(0);      // RevisionNumber
        builder.WriteUInt32(0);      // Flags
        builder.WriteUInt16(0);      // PublicKey (index into #Blob)
        builder.WriteUInt16(0);      // Name (index into #Strings)
        builder.WriteUInt16(0);      // Culture (index into #Strings)
        
        data = builder.ToArray();
        return data.Length;
    }

    private void WriteCorHeader(BlobBuilder builder, SectionLocation location, int metadataOffset, int metadataSize)
    {
        // Write COR20 header (ECMA-335 §II.25.3.3)
        int startOffset = builder.Count;
        
        builder.WriteUInt32(72); // cb (size of header)
        builder.WriteUInt16(2);  // MajorRuntimeVersion
        builder.WriteUInt16(5);  // MinorRuntimeVersion
        
        // MetaData directory (RVA and size)
        builder.WriteUInt32((uint)(location.RelativeVirtualAddress + metadataOffset)); // MetaData.VirtualAddress
        builder.WriteUInt32((uint)metadataSize); // MetaData.Size
        
        // Flags
        builder.WriteUInt32((uint)_corFlags);
        
        // EntryPoint (token or RVA)
        if (_entryPoint.IsNil)
        {
            builder.WriteUInt32(0);
        }
        else
        {
            builder.WriteInt32(MetadataTokens.GetToken(_entryPoint));
        }
        
        // Resources, StrongNameSignature, CodeManagerTable, VTableFixups, ExportAddressTableJumps, ManagedNativeHeader
        // All zeros for IL-only assemblies
        builder.WriteUInt32(0); // Resources.VirtualAddress
        builder.WriteUInt32(0); // Resources.Size
        builder.WriteUInt32(0); // StrongNameSignature.VirtualAddress
        builder.WriteUInt32(0); // StrongNameSignature.Size
        builder.WriteUInt32(0); // CodeManagerTable.VirtualAddress
        builder.WriteUInt32(0); // CodeManagerTable.Size
        builder.WriteUInt32(0); // VTableFixups.VirtualAddress
        builder.WriteUInt32(0); // VTableFixups.Size
        builder.WriteUInt32(0); // ExportAddressTableJumps.VirtualAddress
        builder.WriteUInt32(0); // ExportAddressTableJumps.Size
        builder.WriteUInt32(0); // ManagedNativeHeader.VirtualAddress
        builder.WriteUInt32(0); // ManagedNativeHeader.Size
        
        System.Diagnostics.Debug.Assert(builder.Count - startOffset == 72);
    }

    protected override PEDirectoriesBuilder GetDirectories()
    {
        return _peDirectoriesBuilder;
    }
}
