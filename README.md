# r2rstrip

Strips ReadyToRun (R2R) native images from .NET PE assemblies, producing IL-only output with all metadata preserved.

## Installation

### As a .NET global tool

```bash
dotnet tool install -g r2rstrip
```

### As a NuGet source package

```bash
dotnet add package r2rstrip.lib
```

This adds the source files directly to your project — no extra DLL dependency.

## Usage

### Command-line tool

```bash
r2rstrip <input-r2r-file> <output-file>
r2rstrip -v <input-r2r-file> <output-file>   # verbose
```

### Library API

```csharp
using R2RStrip;

// Strip an R2R assembly to an IL-only assembly
R2RStripper.Strip("input.dll", "output.dll");

// With verbose output
R2RStripper.Strip("input.dll", "output.dll", verbose: true);
```

## What it does

R2R (ReadyToRun) assemblies contain both IL bytecode and pre-compiled native code. `r2rstrip` removes the native code and R2R headers, producing a clean IL-only assembly with all metadata tables, IL method bodies, resources, and signatures preserved.

## License

AGPL-3.0
