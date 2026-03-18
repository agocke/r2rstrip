# r2rstrip

Strips ReadyToRun (R2R) native images from .NET PE assemblies, producing IL-only output with all metadata preserved.

## Installation

```bash
dotnet tool install -g r2rstrip
```

## Usage

```bash
r2rstrip <input-r2r-file> <output-file>
r2rstrip -v <input-r2r-file> <output-file>   # verbose
```

## What it does

R2R (ReadyToRun) assemblies contain both IL bytecode and pre-compiled native code. `r2rstrip` removes the native code and R2R headers, producing a clean IL-only assembly with all metadata tables, IL method bodies, resources, and signatures preserved.

## License

AGPL-3.0
