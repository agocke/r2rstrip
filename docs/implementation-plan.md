# r2rstrip Implementation Plan: Direct Metadata & IL Copy

**Goal:** Convert R2R assemblies to IL-only by copying metadata and IL verbatim, bypassing `MetadataBuilder` to preserve exact heap structure.

## Overview

Based on analysis of the ReadyToRun format and System.Reflection.Metadata source:
- R2R files contain **complete IL and metadata** (not stripped!)
- We can **extract and reuse** the IL stream byte-for-byte
- We can **copy metadata tables** with only RVA adjustments needed
- We can **reuse PEBuilder** for PE file construction

## Architecture

```
R2R Assembly (input)
    ↓
Extract Components
├─ IL Stream (raw bytes)
├─ Metadata Tables (raw bytes)
├─ #String heap (raw bytes)
├─ #Blob heap (raw bytes)
├─ #GUID heap (raw bytes)
└─ #US heap (raw bytes)
    ↓
Adjust MethodDef RVAs
(only change: update method body pointers)
    ↓
Build New PE File
├─ Use PEBuilder for PE structure
├─ Custom metadata serialization
└─ Copy IL stream as-is
    ↓
IL-only Assembly (output)
```

## Components to Implement

### Phase 1: Metadata & IL Extraction

**File:** `src/RawMetadataExtractor.cs`

```csharp
class RawMetadataExtractor
{
    private readonly PEReader _peReader;
    private readonly MetadataReader _reader;
    
    // Extract complete heaps as raw bytes
    public byte[] ExtractStringHeap();
    public byte[] ExtractBlobHeap();
    public byte[] ExtractGuidHeap();
    public byte[] ExtractUserStringHeap();
    
    // Extract IL stream
    public byte[] ExtractILStream();
    public int GetILStreamRVA();
    
    // Extract metadata table bytes
    public byte[] ExtractMetadataTableBytes();
    
    // Get metadata sizes for PE building
    public MetadataSizes CalculateMetadataSizes();
}
```

**Implementation details:**
- Use `MetadataReader.GetHeapMetadataOffset(HeapIndex)` to find heap locations
- Use `MetadataReader.GetHeapSize(HeapIndex)` to determine sizes
- Use `PEReader.GetMetadata().GetContent()` to access raw metadata bytes
- Extract IL by finding first/last method RVA and reading that range

### Phase 2: MethodDef RVA Adjustment

**File:** `src/MethodDefPatcher.cs`

```csharp
class MethodDefPatcher
{
    // Update RVAs in MethodDef table rows
    public byte[] AdjustMethodDefRVAs(
        byte[] methodDefTableBytes,
        int rowCount,
        int rowSize,
        int oldILStreamRVA,
        int newILStreamRVA);
    
    // Calculate MethodDef row size based on metadata
    private int CalculateMethodDefRowSize(
        MetadataSizes sizes,
        int stringHeapSize,
        int blobHeapSize);
}
```

**MethodDef table structure (ECMA-335):**
```
Column         | Size | Description
---------------|------|-------------
RVA            | 4    | Pointer to IL method body
ImplFlags      | 2    | Method implementation flags
Flags          | 2    | Method attribute flags
Name           | 2/4  | Index into #Strings heap
Signature      | 2/4  | Index into #Blob heap
ParamList      | 2/4  | Index into Param table
```

**RVA adjustment:**
```csharp
for each row in MethodDef table:
    oldRVA = read 4 bytes at row offset
    if (oldRVA != 0):  // 0 means abstract/extern/PInvoke
        newRVA = oldRVA - oldILStreamRVA + newILStreamRVA
        write newRVA back to row
```

### Phase 3: Custom Metadata Serialization

**File:** `src/RawMetadataRootBuilder.cs`

```csharp
class RawMetadataRootBuilder
{
    private readonly byte[] _stringHeap;
    private readonly byte[] _blobHeap;
    private readonly byte[] _guidHeap;
    private readonly byte[] _userStringHeap;
    private readonly byte[] _metadataTableBytes;
    private readonly MetadataSizes _sizes;
    
    public RawMetadataRootBuilder(
        byte[] stringHeap,
        byte[] blobHeap,
        byte[] guidHeap,
        byte[] userStringHeap,
        byte[] metadataTableBytes,
        MetadataSizes sizes);
    
    // Serialize complete metadata root
    public void Serialize(
        BlobBuilder builder,
        int methodBodyStreamRva,
        int mappedFieldDataStreamRva);
    
    private void WriteMetadataHeader(BlobBuilder builder);
    private void WriteStreamHeaders(BlobBuilder builder);
    private void WriteTableStream(BlobBuilder builder);
    private void WriteHeaps(BlobBuilder builder);
}
```

**Metadata root structure (ECMA-335 §II.24.2.1):**
```
Metadata root:
├─ Signature: 0x424A5342 ("BSJB")
├─ MajorVersion: 1
├─ MinorVersion: 1
├─ Reserved: 0
├─ Version string length (padded to 4-byte boundary)
├─ Version string (e.g., "v4.0.30319")
├─ Flags: 0
├─ Number of streams (typically 5)
├─ Stream headers (for #~, #Strings, #US, #GUID, #Blob)
│
└─ Stream data:
    ├─ #~ (tables stream)
    │   ├─ Table stream header
    │   └─ Table rows (copied verbatim with adjusted MethodDef RVAs)
    ├─ #Strings heap
    ├─ #US heap
    ├─ #GUID heap
    └─ #Blob heap
```

### Phase 4: PE Building with Custom Metadata

**File:** `src/RawMetadataPEBuilder.cs`

```csharp
class RawMetadataPEBuilder : PEBuilder
{
    private readonly RawMetadataRootBuilder _metadataRoot;
    private readonly BlobBuilder _ilStream;
    private readonly MethodDefinitionHandle _entryPoint;
    private readonly PEDirectoriesBuilder _directories;
    
    public RawMetadataPEBuilder(
        PEHeaderBuilder header,
        RawMetadataRootBuilder metadataRoot,
        BlobBuilder ilStream,
        MethodDefinitionHandle entryPoint);
    
    protected override ImmutableArray<Section> CreateSections();
    protected override BlobBuilder SerializeSection(string name, SectionLocation location);
    protected internal override PEDirectoriesBuilder GetDirectories();
    
    private BlobBuilder SerializeTextSection(SectionLocation location);
    private void WriteImportAddressTable(BlobBuilder builder);
    private void WriteImportDirectory(BlobBuilder builder);
    private void WriteCorHeader(BlobBuilder builder, SectionLocation location);
}
```

**.text section layout:**
```
Offset | Size | Component
-------|------|----------
0x00   | 8    | Import Address Table (IAT)
0x08   | ~60  | Import Directory & tables
0x48   | 72   | COR20 Header
0x90   | var  | IL Stream (method bodies)
...    | var  | Metadata root
```

**Key: COR20 Header must point to metadata and have correct flags:**
```csharp
COR20Header:
    cb: 72
    MajorRuntimeVersion: 2
    MinorRuntimeVersion: 5
    MetaData.VirtualAddress: <metadata RVA>
    MetaData.Size: <metadata size>
    Flags: COMIMAGE_FLAGS_ILONLY (0x00000001)
    EntryPointToken: <entry point MethodDef token or 0>
    // All other fields: 0 (no R2R, no resources, etc.)
```

### Phase 5: Main Assembly Rebuilder

**File:** `src/AssemblyRebuilder.cs` (refactor existing)

```csharp
public class AssemblyRebuilder
{
    public void RebuildAsILOnly(string inputPath, string outputPath)
    {
        using var peReader = new PEReader(File.OpenRead(inputPath));
        var metadataReader = peReader.GetMetadataReader();
        
        // Extract all components
        var extractor = new RawMetadataExtractor(peReader, metadataReader);
        var stringHeap = extractor.ExtractStringHeap();
        var blobHeap = extractor.ExtractBlobHeap();
        var guidHeap = extractor.ExtractGuidHeap();
        var userStringHeap = extractor.ExtractUserStringHeap();
        var ilBytes = extractor.ExtractILStream();
        var oldILRva = extractor.GetILStreamRVA();
        var tableBytes = extractor.ExtractMetadataTableBytes();
        
        // Adjust MethodDef RVAs
        var patcher = new MethodDefPatcher();
        var fixedTableBytes = patcher.AdjustMethodDefRVAs(
            tableBytes, 
            oldILRva, 
            newILRva: CalculateNewILRVA());
        
        // Build metadata root
        var metadataRoot = new RawMetadataRootBuilder(
            stringHeap, blobHeap, guidHeap, userStringHeap,
            fixedTableBytes, sizes);
        
        // Build IL stream
        var ilStream = new BlobBuilder();
        ilStream.WriteBytes(ilBytes);
        
        // Build PE
        var peBuilder = new RawMetadataPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: ...),
            metadataRoot,
            ilStream,
            entryPoint);
        
        // Serialize
        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        
        using var output = File.Create(outputPath);
        peBlob.WriteContentTo(output);
    }
}
```

## Implementation Phases

### Phase 0: Bootstrap - Minimal Valid PE ✓ (COMPLETED)
- [x] Create `RawMetadataPEBuilder` extending `PEBuilder`
- [x] Write minimal CLI metadata header (BSJB signature, version, streams)
- [x] Write minimal metadata tables (Module and Assembly tables only)
- [x] Write minimal heaps (#Strings, #Blob, #GUID, #US with just null bytes)
- [x] Write proper COR20 header pointing to metadata
- [x] Create `.text` section with IAT, metadata, and COR20 header
- [x] Create tests for `RawMetadataPEBuilder` (valid PE verification)
- [x] Refactor `Program.StripR2R()` to use `AssemblyRebuilder`
- [x] Verify tests correctly fail (no types/strings copied yet)

**Status:** ✅ We can now write a valid minimal managed PE file using hand-crafted metadata structures, without `MetadataBuilder`. Foundation is in place for copying real metadata.

### Phase 1: Copy Metadata Heaps ✓ (COMPLETED)
- [x] Create comprehensive tests for all four heaps
- [x] Update `RawMetadataPEBuilder` to accept source `MetadataReader` and `PEReader`
- [x] Implement heap extraction from source assembly:
  - [x] Extract #Strings heap raw bytes
  - [x] Extract #Blob heap raw bytes
  - [x] Extract #GUID heap raw bytes
  - [x] Extract #US (user strings) heap raw bytes
- [x] Update `WriteMinimalMetadata()` to write copied heaps instead of minimal ones
- [x] Test: verify all heap tests pass (`TestApp_Preserves*Heap`)

**Status:** ✅ All four metadata heaps are now copied byte-for-byte from source to output. All heap preservation tests pass!

### Phase 2: Copy Metadata Tables (2-3 hours)
- [ ] Extract metadata tables stream (#~) from source assembly
- [ ] Parse table stream header (valid/sorted bitmasks, row counts)
- [ ] Copy table rows verbatim (initially without RVA adjustments)
- [ ] Write complete tables stream to output
- [ ] Test: verify `TestApp_PreservesTypeDefinitions` passes

**Goal:** Copy all metadata tables to preserve types, methods, fields, etc.

### Phase 3: IL Stream & MethodDef RVA Adjustment (3-4 hours)
- [ ] Extract IL stream from source assembly
- [ ] Implement `MethodDefPatcher`:
  - [ ] Calculate MethodDef table row size
  - [ ] Parse MethodDef RVAs
  - [ ] Adjust RVAs based on new IL stream location
- [ ] Include IL stream in `.text` section
- [ ] Update COR20 header for new layout
- [ ] Test: verify ILVerify passes, basic method execution works

**Goal:** Preserve IL method bodies with correct RVAs.

### Phase 4: Complete Integration (1-2 hours)
- [ ] Remove old `MetadataCopier` (no longer needed)
- [ ] Clean up `AssemblyRebuilder` to use only raw copying
- [ ] Run all existing tests
- [ ] Verify all `TestApp_*` tests pass
- [ ] Performance testing and optimization

**Goal:** Clean up codebase, ensure all tests pass.

### Phase 5: Edge Cases & Polish (2-3 hours)
- [ ] Handle assemblies with no entry point
- [ ] Handle abstract methods (RVA = 0)
- [ ] Handle PInvoke methods (RVA = 0)
- [ ] Handle large heaps (>64K indices = 4-byte references)
- [ ] Add comprehensive error handling
- [ ] Documentation and code cleanup

**Goal:** Production-ready implementation.

## Current Status (November 30, 2025)

### ✅ Completed: Phase 0 - Bootstrap
We've successfully created a foundation for writing PE files with hand-crafted metadata:

**Files Created:**
- `src/RawMetadataPEBuilder.cs` - Custom PE builder that writes metadata without `MetadataBuilder`
- `test/r2rstrip.Tests/RawMetadataPEBuilderTests.cs` - Tests for the PE builder

**What Works:**
- ✅ Writing a valid minimal managed PE file
- ✅ Hand-crafted CLI metadata header (BSJB signature, version string, stream headers)
- ✅ Minimal metadata tables (Module and Assembly tables with 1 row each)
- ✅ Minimal heaps (single null byte for #Strings, #Blob, #US)
- ✅ Proper COR20 header with metadata directory pointer
- ✅ `.text` section with IAT, metadata, and COR20 header
- ✅ `RawMetadataPEBuilder` integration into `AssemblyRebuilder`
- ✅ Tests correctly validate we're writing valid PE files

**What's Not Yet Implemented:**
- ❌ Copying actual metadata from source assembly (currently writes minimal empty metadata)
- ❌ Copying types, methods, fields (TypeDef table empty)
- ❌ Copying strings, blobs, GUIDs from heaps
- ❌ Copying IL method bodies
- ❌ Adjusting MethodDef RVAs

**Test Results (After Phase 1):**
- 10 tests passing (✅ All heap tests + basic PE structure)
- 2 tests skipped (method definition/execution - expected for Phase 2/3)
- 2 tests failing (expected - Phase 2 not yet implemented):
  - `TestApp_PreservesTypeDefinitions` - No types copied (need Phase 2)
  - `TestApp_ExecutesWithStubMain` - Invalid assembly name (need Phase 2)

**Heap Tests:** ✅ All 5 heap tests now passing:
- ✅ `TestApp_PreservesStringTable` - #Strings heap preserved (844 bytes)
- ✅ `TestApp_PreservesBlobHeap` - #Blob heap preserved (340 bytes)
- ✅ `TestApp_PreservesGuidHeap` - #GUID heap preserved (16 bytes)
- ✅ `TestApp_PreservesUserStringHeap` - #US heap preserved (192 bytes)
- ✅ `TestApp_AllMetadataHeapsPreserved` - Comprehensive test passes

### 🎯 Next Steps: Phase 2 - Copy Metadata Tables

The immediate next task is to copy the metadata tables from the source assembly.

**What We Have Now:**
- ✅ Valid minimal PE file structure
- ✅ All four metadata heaps copied byte-for-byte
- ❌ Metadata tables (still using minimal Module + Assembly tables)

**Action Items for Phase 2:**
1. Extract metadata tables stream (#~) raw bytes from source assembly
2. Parse table stream header (valid/sorted bitmasks, row counts)
3. Replace minimal tables with actual table data
4. Write complete tables stream to output
5. Verify `TestApp_PreservesTypeDefinitions` passes

Once Phase 2 is complete, we'll have all types, methods, and fields from the source assembly.

---

## Expected Outcomes

After implementation:
- ✅ String heap copied byte-for-byte (preserves handles)
- ✅ All metadata heaps preserved exactly
- ✅ All metadata tables preserved (with adjusted MethodDef RVAs)
- ✅ IL stream copied verbatim
- ✅ Valid IL-only PE file
- ✅ No dependency on `MetadataBuilder` suffix-sorting
- ✅ Complete control over metadata layout
- ✅ Smaller, cleaner codebase (removed `MetadataCopier`)

## Testing Strategy

1. **Unit tests for each component:**
   - `RawMetadataExtractor`: verify extracted bytes match expected
   - `MethodDefPatcher`: verify RVA calculations
   - `RawMetadataRootBuilder`: verify serialized metadata matches original

2. **Integration tests:**
   - Existing `TestApp_*` tests should all pass
   - Verify `TestApp_PreservesStringTable` still passes
   - Verify ILVerify passes
   - Verify assembly loads and executes

3. **Edge cases:**
   - Assembly with no entry point
   - Assembly with abstract methods (RVA = 0)
   - Assembly with PInvoke methods (RVA = 0)
   - Large assemblies with >64K strings (4-byte heap indices)

## References

- [ReadyToRun Format](./readytorun-overview.md)
- [ECMA-335 CLI Specification](https://www.ecma-international.org/publications-and-standards/standards/ecma-335) (§II.24: Metadata Physical Layout)
- System.Reflection.Metadata source:
  - `ManagedPEBuilder.cs` - PE layout reference
  - `MetadataBuilder.cs` - table/heap format reference
  - `MetadataRootBuilder.cs` - metadata root format

## Success Criteria

1. All existing tests pass
2. String heap matches byte-for-byte
3. Output assembly passes ILVerify
4. Output assembly executes correctly
5. Code is simpler and more maintainable than current approach
