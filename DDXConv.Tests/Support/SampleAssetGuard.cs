using Xunit;

namespace DDXConv.Tests.Support;

/// <summary>
///     Skip guard for tests that need the extracted Xbox textures and PC references under
///     <c>Sample/</c>. Other Sample content, retained output images and packed build archives
///     do not supply these fixtures. Synthetic tests never use this.
/// </summary>
internal static class SampleAssetGuard
{
    private static readonly Lazy<string?> RepoRootLazy = new(() => FindRepoRoot(AppContext.BaseDirectory));

    internal static string? RepoRoot => RepoRootLazy.Value;

    /// <summary>Skips when neither extracted texture corpus is present; incomplete corpora still fail their file assertions.</summary>
    internal static string RequireSampleRoot()
    {
        Assert.SkipWhen(RepoRoot is null,
            "Extracted Xbox .ddx textures and PC .dds references were not found under Sample/ " +
            "(private fixtures; packed archives and retained output images are not sufficient)");
        return RepoRoot!;
    }

    internal static string? FindRepoRoot(string startingDirectory)
    {
        var dir = new DirectoryInfo(startingDirectory);
        while (dir != null)
        {
            // Either side establishes a fixture corpus. Do not silently skip a partially
            // extracted corpus: the tests retain their assertions for every required file.
            if (HasFiles(Path.Combine(dir.FullName, "Sample", "Textures", "textures_360_final", "textures"), ".ddx") ||
                HasFiles(Path.Combine(dir.FullName, "Sample", "Unpacked_Builds", "PC_Final_Unpacked", "Data", "textures"), ".dds"))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    private static bool HasFiles(string directory, string extension)
        => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase));
}
