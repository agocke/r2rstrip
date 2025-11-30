# ReadyToRun (R2R) Format Overview

**Source:** https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-format.md

This document summarizes the key aspects of ReadyToRun format relevant to the r2rstrip project.

## What is ReadyToRun?

ReadyToRun (R2R) is a hybrid file format that contains:
- **Complete ECMA-335 metadata and IL** (same as regular .NET assemblies)
- **Pre-compiled native code** for improved startup performance
- **Additional R2R-specific structures** to support native code execution

## PE File Structure

R2R assemblies are valid PE/COFF files with the following characteristics:

### COR Header Flags
- `COMIMAGE_FLAGS_IL_LIBRARY` (0x00000004) is set
- `ManagedNativeHeader` field points to the `READYTORUN_HEADER` structure

### Key Insight for r2rstrip
**R2R files contain the complete original IL and metadata.** The IL is preserved to enable:
1. Fallback to JIT compilation if native code can't be used
2. Cross-platform compatibility
3. Metadata reflection

## File Layout Comparison

### IL-only Assembly
```
.text section:
├─ Import Address Table (~8 bytes)
├─ Import Directory (~60 bytes)
├─ COR20 Header (72 bytes)
├─ IL Stream (method bodies)
└─ Metadata (tables + heaps)
```

### R2R Assembly
```
.text section:
├─ Import Address Table (~8 bytes)
├─ Import Directory (~60 bytes)
├─ R2R Header (148+ bytes)           ← Extra R2R structures
├─ R2R Method Lookup Tables          ← Maps methods to native code
├─ COR20 Header (72 bytes)
├─ IL Stream (method bodies)         ← Still present!
└─ Metadata (tables + heaps)

Additional sections:
├─ Native code sections
├─ R2R runtime function table
└─ R2R exception handling info
```

## READYTORUN_HEADER Structure

The R2R header is located via the `ManagedNativeHeader` directory in the COR header:

```c
struct READYTORUN_HEADER
{
    DWORD   Signature;      // 0x00525452 (ASCII "RTR")
    USHORT  MajorVersion;   // Current: 9
    USHORT  MinorVersion;
    READYTORUN_CORE_HEADER CoreHeader;
}

struct READYTORUN_CORE_HEADER
{
    DWORD   Flags;              // READYTORUN_FLAG_XXX
    DWORD   NumberOfSections;
    // Followed by array of READYTORUN_SECTION entries
}
```

### Important Flags
- `READYTORUN_FLAG_PLATFORM_NEUTRAL_SOURCE` (0x00000001)
- `READYTORUN_FLAG_COMPOSITE` (0x00000002) - Composite R2R file
- `READYTORUN_FLAG_EMBEDDED_MSIL` (0x00000010) - IL embedded in R2R
- `READYTORUN_FLAG_MULTIMODULE_VERSION_BUBBLE` (0x00000040)

## Key R2R Sections

R2R files contain various sections describing the native code and supporting structures:

### Sections We Care About for Stripping

| Section | Type | Description |
|---------|------|-------------|
| CompilerIdentifier | 100 | Compiler version string |
| ImportSections | 101 | Fixup slots for lazy binding |
| RuntimeFunctions | 102 | Native method code locations & unwind info |
| MethodDefEntryPoints | 103 | Maps MethodDef → native code entry point |
| ExceptionInfo | 104 | Native exception handling tables |
| DebugInfo | 105 | Debug info for native code |

### What Gets Removed When Stripping

When converting R2R → IL-only, we remove:
- ✗ R2R header and all R2R sections
- ✗ Native code sections
- ✗ Runtime function tables
- ✗ Native exception handling info

### What We Preserve

- ✓ Original IL method bodies (already in the file!)
- ✓ Complete metadata (all tables and heaps)
- ✓ String heap (byte-for-byte)
- ✓ Blob heap
- ✓ GUID heap
- ✓ User String heap

## MethodDef Table and RVAs

The MethodDef metadata table has this structure:

```
Row format (per ECMA-335):
| RVA (4 bytes) | ImplFlags (2) | Flags (2) | Name (2-4) | Signature (2-4) | ParamList (2-4) |
```

### The RVA Problem

**In R2R assemblies:**
- MethodDef RVAs point to **IL method bodies** (not native code!)
- The IL is at a different file offset due to R2R header overhead
- Example: IL starts at RVA `0x00010D28` in R2R file

**In IL-only assemblies:**
- MethodDef RVAs point to **IL method bodies**
- IL starts at a different RVA due to smaller header
- Example: IL starts at RVA `0x00002048` in IL-only file

**Why RVAs differ:**
- R2R has ~2700 bytes of R2R-specific structures before IL
- IL-only has ~150 bytes of PE/import tables before IL
- Same IL bytes, different addresses → need to update RVAs

### Solution for r2rstrip

When building an IL-only file from R2R:
1. **Extract IL stream** - it's already in the R2R file
2. **Copy metadata tables** - preserve all rows
3. **Calculate new RVAs** - adjust based on new IL stream location
4. **Update MethodDef table** - write new RVAs back to table

The RVA adjustment is simple:
```
newRVA = oldRVA - oldILStreamRVA + newILStreamRVA
```

## Import Sections and Fixups

R2R files use "fixup" mechanisms for lazy initialization of references. These are described by:

- **Import Sections** - Arrays of slots that get filled at runtime
- **Signatures** - Describe what each slot should contain
- **Fixup Types** - Various kinds of references (methods, types, fields, etc.)

For IL-only assemblies, we don't need these - the JIT handles all resolution.

## References

Full specification:
- [ReadyToRun Format Documentation](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/botr/readytorun-format.md)
- [readytorun.h Header File](https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/readytorun.h)
- [ECMA-335 CLI Specification](https://www.ecma-international.org/publications-and-standards/standards/ecma-335)
