#!/bin/bash
set -e

echo "=== Building r2rstrip test suite ==="
echo ""

# Build testapp first (both IL-only and R2R versions)
echo "Step 1: Building testapp (IL-only)..."
cd testapp
dotnet build -c Release > /dev/null 2>&1
echo "  ✓ testapp IL-only built"

echo "Step 2: Publishing testapp (R2R)..."
dotnet publish -c Release > /dev/null 2>&1
echo "  ✓ testapp R2R published"

# Build and run tests (project reference will build r2rstrip)
echo "Step 3: Building and running tests..."
cd ../r2rstrip.Tests
dotnet test -c Release

# Show summary
echo ""
echo "=== Test artifacts ==="
echo "  testapp IL-only: $(ls -lh ../testapp/bin/Release/net10.0/testapp.dll | awk '{print $5}')"
echo "  testapp R2R:     $(ls -lh ../testapp/bin/Release/net10.0/publish/testapp.dll | awk '{print $5}')"
echo "  r2rstrip:        $(ls -lh ../../artifacts/bin/r2rstrip/release/r2rstrip | awk '{print $5}')"
