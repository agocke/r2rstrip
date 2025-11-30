# Phase 1: Copy Metadata Heaps - Summary

## Confirmation: Metadata Heaps Do NOT Contain RVA Pointers ✅

**Yes, confirmed!** Metadata heaps can be copied byte-for-byte without any adjustments.

### Metadata Heaps (No RVAs)
All four metadata heaps contain data that uses **indices/offsets**, not RVAs:

1. **#Strings heap**: Null-terminated UTF-8 strings
2. **#Blob heap**: Length-prefixed byte sequences (signatures, custom attributes, etc.)
3. **#GUID heap**: Array of 16-byte GUIDs (module MVIDs, etc.)
4. **#US heap**: Length-prefixed UTF-16 strings (string literals in code)

### Where RVAs Actually Exist
RVA pointers only exist in **metadata tables**:

1. **MethodDef table (0x06)**: 4-byte RVA field pointing to IL method bodies
   - This is what Phase 3 will handle
   - Most common RVA usage

2. **FieldRVA table (0x1D)**: 4-byte RVA field pointing to initial field data
   - Used for fields with `[FieldOffset]` or mapped data
   - Relatively rare (testapp doesn't have any)
   - Will need to be handled in Phase 3 as well

## Tests Added ✅

I've added comprehensive tests for all four metadata heaps:

### New Test Methods in `TestAppTests.cs`

1. **`TestApp_PreservesStringTable()`** - Verifies #Strings heap (existing)
2. **`TestApp_PreservesBlobHeap()`** - Verifies #Blob heap (NEW)
3. **`TestApp_PreservesGuidHeap()`** - Verifies #GUID heap (NEW)
4. **`TestApp_PreservesUserStringHeap()`** - Verifies #US heap (NEW)
5. **`TestApp_AllMetadataHeapsPreserved()`** - Comprehensive test for all heaps (NEW)

### New Helper Methods in `TestHelpers.cs`

1. **`GetBlobHeap(string assemblyPath)`** - Extract #Blob heap bytes
2. **`GetGuidHeap(string assemblyPath)`** - Extract #GUID heap bytes
3. **`GetUserStringHeap(string assemblyPath)`** - Extract #US heap bytes
4. **`AssertBlobHeapsMatch(...)`** - Compare #Blob heaps
5. **`AssertGuidHeapsMatch(...)`** - Compare #GUID heaps
6. **`AssertUserStringHeapsMatch(...)`** - Compare #US heaps
7. **`AssertHeapMatch(...)`** - Generic byte-for-byte heap comparison

## Current Test Results

All new heap tests are **failing as expected** because we haven't implemented Phase 1 yet:

```
TestApp_PreservesStringTable [FAIL]
  #Strings heap mismatch: expected 844 bytes, got 1 bytes

TestApp_PreservesBlobHeap [FAIL]
  #Blob heap mismatch: expected 340 bytes, got 1 bytes

TestApp_PreservesGuidHeap [FAIL]
  (not yet run, but will fail)

TestApp_PreservesUserStringHeap [FAIL]
  (not yet run, but will fail)
```

Currently, `RawMetadataPEBuilder` writes minimal 1-byte heaps (just a null byte). This is correct for a minimal PE, but we need to copy the real heaps from the source assembly.

## Phase 1 Implementation Plan

Now that we've confirmed heaps don't need RVA adjustments and have tests in place, Phase 1 can proceed with confidence:

### Step 1: Extract Heaps from Source Assembly
Add methods to extract raw heap bytes:
```csharp
public static byte[] ExtractStringHeap(MetadataReader reader, PEReader peReader)
{
    int offset = reader.GetHeapMetadataOffset(HeapIndex.String);
    int size = reader.GetHeapSize(HeapIndex.String);
    var metadata = peReader.GetMetadata().GetContent();
    // Copy bytes from offset to offset+size
}

// Similar for Blob, Guid, UserString
```

### Step 2: Pass Heaps to RawMetadataPEBuilder
Modify constructor to accept heap data:
```csharp
public RawMetadataPEBuilder(
    PEHeaderBuilder header,
    byte[] stringHeap,
    byte[] blobHeap,
    byte[] guidHeap,
    byte[] userStringHeap,
    CorFlags corFlags = CorFlags.ILOnly,
    MethodDefinitionHandle entryPoint = default)
```

### Step 3: Write Real Heaps Instead of Minimal Ones
Update `WriteMinimalMetadata()` to write the actual heap bytes instead of single null bytes.

### Step 4: Verify Tests Pass
Once implemented, all 5 heap tests should pass:
- ✅ TestApp_PreservesStringTable
- ✅ TestApp_PreservesBlobHeap
- ✅ TestApp_PreservesGuidHeap
- ✅ TestApp_PreservesUserStringHeap
- ✅ TestApp_AllMetadataHeapsPreserved

## Benefits of Phase 1

1. **No RVA adjustments needed** - Heaps can be copied verbatim
2. **Simple implementation** - Just extract and write bytes
3. **Comprehensive test coverage** - All 4 heaps tested
4. **Foundation for Phase 2** - Once heaps work, we can copy tables
5. **Preserves exact structure** - Byte-for-byte identical to source

## Next Steps

Ready to implement Phase 1! The tests are in place and will guide the implementation.
