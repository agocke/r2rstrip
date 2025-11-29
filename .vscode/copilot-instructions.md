# GitHub Copilot Instructions for r2rstrip Project

## Running C# Scripts

When you need to run quick C# exploration or test code:

**USE**: `dotnet run <file.cs>`

Example:
```bash
dotnet run test_heap.cs
```

**DO NOT USE**:
- ❌ `dotnet script` (requires dotnet-script global tool)
- ❌ `.csx` files (C# scripting format)
- ❌ Creating temporary `.csproj` files
- ❌ `csc` compiler directly

## Rationale

`dotnet run <file.cs>` is the standard .NET approach that:
- Works out of the box with .NET SDK
- Requires no additional tools or setup
- Handles dependencies automatically
- Is the simplest and most portable option

## Reading and writing .NET assemblies

When you need to read or write .NET assemblies, use the `System.Reflection.Metadata` and `System.Reflection.PortableExecutable` namespaces. These libraries provide low-level structured access to the metadata and PE structure of .NET assemblies. However, the operations of r2rstrip may not want structured access in some cases, as the goal is to preserve as much of the original binary data as possible. In that case, direct byte access, or using blob memory directly, may be more appropriate.

The source code for `System.Reflection.Metadata` is available here: `~/code/runtime/src/libraries/System.Reflection.Metadata/src/System/Reflection/Metadata`. Feel free to use it as a reference for understanding how to parse and manipulate .NET metadata. If no public API exists for your use case, you can look at the source code to see how it is implemented and potentially replicate similar logic in your own code. For example, if you need to access a metadata heap that is not exposed by the public API, you can look at how the library reads that heap and implement similar logic.