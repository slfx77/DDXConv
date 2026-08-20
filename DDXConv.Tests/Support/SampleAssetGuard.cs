using Xunit;

namespace DDXConv.Tests.Support;

/// <summary>
///     Skip guard for tests that need the real game-asset corpus under the repo's
///     <c>Sample/</c> directory. Sample is not in git, so CI runners never have it — before
///     this guard existed those tests THREW on the missing directory, which meant the DDXConv
///     CI step had never actually been green. Synthetic tests never use this.
/// </summary>
internal static class SampleAssetGuard
{
    private static readonly Lazy<string?> RepoRootLazy = new(FindRepoRoot);

    internal static string? RepoRoot => RepoRootLazy.Value;

    /// <summary>Skips the calling test when the Sample corpus is not present.</summary>
    internal static string RequireSampleRoot()
    {
        Assert.SkipWhen(RepoRoot is null,
            "Sample/ directory with real game assets not found (not in git; expected only on dev machines)");
        return RepoRoot!;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Sample")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}
