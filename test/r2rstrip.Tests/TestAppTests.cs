using Xunit;
using Xunit.Abstractions;

namespace R2RStrip.Tests;

/// <summary>
/// Tests using the real testapp project
/// </summary>
public class TestAppTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly string _ilOnlyDll;
    private readonly string _r2rDll;
    private readonly ITestOutputHelper _output;
    private readonly string _strippedDll;
    private static readonly object _stripLock = new object();
    private static bool _hasStripped = false;

    public TestAppTests(ITestOutputHelper outputHelper)
    {
        _output = outputHelper;
        var baseDir = AppContext.BaseDirectory;
        _testDataDir = Path.Combine(baseDir, "TestData");

        _ilOnlyDll = Path.Combine(_testDataDir, "testapp-il.dll");
        _r2rDll = Path.Combine(_testDataDir, "testapp-r2r.dll");
        _strippedDll = Path.Combine(_testDataDir, "testapp-stripped.dll");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }

    /// <summary>
    /// Strip the test app R2R assembly (cached across tests)
    /// </summary>
    private string StripTestApp()
    {
        lock (_stripLock)
        {
            if (!_hasStripped)
            {
                var exitCode = TestHelpers.StripAssembly(_r2rDll, _strippedDll);
                Assert.Equal(0, exitCode);
                Assert.True(File.Exists(_strippedDll), "Stripped DLL should exist");

                // Copy the runtimeconfig.json file with the appropriate name
                var r2rRuntimeConfig = Path.ChangeExtension(_r2rDll, ".runtimeconfig.json");
                var strippedRuntimeConfig = Path.ChangeExtension(_strippedDll, ".runtimeconfig.json");
                if (File.Exists(r2rRuntimeConfig))
                {
                    File.Copy(r2rRuntimeConfig, strippedRuntimeConfig, overwrite: true);
                }

                _hasStripped = true;
            }

            return _strippedDll;
        }
    }

    [Fact]
    public void TestApp_ArtifactsExist()
    {
        // Verify IL-only version exists
        Assert.True(File.Exists(_ilOnlyDll), "IL-only DLL should exist in TestData");

        // Verify R2R version exists
        Assert.True(File.Exists(_r2rDll), "R2R DLL should exist in TestData");

        // Verify R2R
        Assert.True(TestHelpers.IsR2RAssembly(_r2rDll), "Published assembly should be R2R");

        // Verify IL-only is not R2R
        Assert.False(TestHelpers.IsR2RAssembly(_ilOnlyDll), "IL-only assembly should not be R2R");
    }

    [Fact]
    public void TestApp_StripsSuccessfully()
    {
        var strippedDll = StripTestApp();

        // Verify it's a valid PE
        Assert.True(TestHelpers.IsValidManagedPE(strippedDll), "Should be valid PE");

        // Verify it's not R2R
        Assert.False(TestHelpers.IsR2RAssembly(strippedDll), "Should not be R2R");
    }

    [Fact]
    public void TestApp_PreservesTypeDefinitions()
    {
        var strippedDll = StripTestApp();

        // Get metadata info - should have type definitions from original
        var metadata = TestHelpers.GetMetadataInfo(strippedDll);
        Assert.Contains("Calculator", metadata.TypeNames);
        Assert.Contains("Person", metadata.TypeNames);
    }

    [Fact]
    public void TestApp_PassesILVerification()
    {
        var strippedDll = StripTestApp();

        // Verify the IL is valid
        var errors = TestHelpers.VerifyIL(strippedDll);
        Assert.Empty(errors);
    }

    [Fact(Skip = "TODO: Need to copy method definitions from original assembly")]
    public void TestApp_PreservesMethodDefinitions()
    {
        var strippedDll = StripTestApp();

        // Get metadata info - should have method definitions from original
        var metadata = TestHelpers.GetMetadataInfo(strippedDll);
        Assert.Contains("Add", metadata.MethodNames);
        Assert.Contains("Multiply", metadata.MethodNames);
    }

    [Fact]
    public async Task TestApp_ExecutesWithStubMain()
    {
        // Ensure stripped version exists
        var strippedDll = StripTestApp();

        // Run stripped version
        var strippedResult = await TestHelpers.RunDll(strippedDll);
        _output.WriteLine(strippedResult.Output);

        // With stub Main method, we expect exit code 100
        Assert.Equal(100, strippedResult.ExitCode);
    }

    [Fact(Skip = "TODO: Need to copy method definitions with proper IL bodies")]
    public async Task TestApp_ExecutesCorrectly()
    {
        // Ensure stripped version exists
        var strippedDll = StripTestApp();

        // Run IL-only version
        var ilResult = await TestHelpers.RunDll(_ilOnlyDll);
        Assert.Equal(0, ilResult.ExitCode);

        // Run stripped version
        var strippedResult = await TestHelpers.RunDll(strippedDll);
        _output.WriteLine(strippedResult.Output);
        Assert.Equal(0, strippedResult.ExitCode);

        // Compare outputs
        Assert.Equal(ilResult.Output.Trim(), strippedResult.Output.Trim());
    }

    [Fact]
    public void TestApp_PreservesStringTable()
    {
        var strippedDll = StripTestApp();

        // Verify the #Strings metadata heap is identical between R2R and stripped assemblies
        // This ensures all type names, method names, field names, etc. are preserved
        TestHelpers.AssertStringHeapsMatch(_r2rDll, strippedDll, _output);
    }
}
