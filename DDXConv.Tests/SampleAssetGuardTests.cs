using DDXConv.Tests.Support;
using Xunit;

namespace DDXConv.Tests;

public sealed class SampleAssetGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ddxconv_samples_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnrelatedSampleContentDoesNotEnableCorpusTests()
    {
        var start = CreateDirectory("checkout", "bin");
        WriteFile("checkout", "Sample", "Builds", "Fallout - Textures.bsa");
        WriteFile("checkout", "Sample", "other", "texture.ddx");

        Assert.Null(SampleAssetGuard.FindRepoRoot(start));
    }

    [Fact]
    public void EmptyExtractionDirectoriesAndRetainedOutputsDoNotEnableCorpusTests()
    {
        var start = CreateDirectory("checkout", "bin");
        WriteFile("checkout", "Sample", "Textures", "textures_360_final", "textures", "armor", "converted.dds");
        WriteFile("checkout", "Sample", "Unpacked_Builds", "PC_Final_Unpacked", "Data", "textures", "retained_mip0.png");

        Assert.Null(SampleAssetGuard.FindRepoRoot(start));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EitherExtractedCorpusEnablesFileAssertionsForIncompleteFixtures(bool xbox, bool pc)
    {
        var start = CreateDirectory("checkout", "bin");
        if (xbox) WriteFile("checkout", "Sample", "Textures", "textures_360_final", "textures", "one.DDX");
        if (pc) WriteFile("checkout", "Sample", "Unpacked_Builds", "PC_Final_Unpacked", "Data", "textures", "one.DDS");

        Assert.Equal(Path.Combine(_root, "checkout"), SampleAssetGuard.FindRepoRoot(start));
    }

    [Fact]
    public void UnrelatedNearestSampleDoesNotHideAnAncestorCorpus()
    {
        var start = CreateDirectory("checkout", "src", "DDXConv", "bin");
        CreateDirectory("checkout", "src", "DDXConv", "Sample");
        WriteFile("checkout", "Sample", "Textures", "textures_360_final", "textures", "characters", "eye.ddx");

        Assert.Equal(Path.Combine(_root, "checkout"), SampleAssetGuard.FindRepoRoot(start));
    }

    [Fact]
    public void NearestPartialCorpusIsNotHiddenByAnAncestorCorpus()
    {
        var start = CreateDirectory("checkout", "src", "DDXConv", "bin");
        WriteFile("checkout", "src", "DDXConv", "Sample", "Textures", "textures_360_final", "textures", "partial.ddx");
        WriteFile("checkout", "Sample", "Textures", "textures_360_final", "textures", "characters", "eye.ddx");

        Assert.Equal(Path.Combine(_root, "checkout", "src", "DDXConv"), SampleAssetGuard.FindRepoRoot(start));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine(_root, Path.Combine(parts));
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteFile(params string[] parts)
    {
        var path = Path.Combine(_root, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture marker");
    }
}
