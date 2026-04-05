namespace FirefightDesktop;

internal static class AssetLocator
{
    public static string FindAssetRoot(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var normalized = Path.GetFullPath(overridePath);
            ValidateAssetRoot(normalized);
            return normalized;
        }

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = TryFindAssetRootUnder(cursor.FullName);
            if (candidate is not null)
            {
                return candidate;
            }

            var siblingCandidate = TryFindAssetRootUnder(Path.GetFullPath(Path.Combine(cursor.FullName, "..")));
            if (siblingCandidate is not null)
            {
                return siblingCandidate;
            }

            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the extracted Firefight assets directory. Use --assets.");
    }

    private static void ValidateAssetRoot(string assetRoot)
    {
        if (!LooksLikeAssetRoot(assetRoot))
        {
            throw new DirectoryNotFoundException($"Invalid assets root: {assetRoot}");
        }
    }

    private static string? TryFindAssetRootUnder(string parentDirectory)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return null;
        }

        foreach (var directory in Directory.GetDirectories(parentDirectory))
        {
            var assetsPath = Path.Combine(directory, "assets");
            if (LooksLikeAssetRoot(assetsPath))
            {
                return assetsPath;
            }
        }

        return null;
    }

    private static bool LooksLikeAssetRoot(string assetRoot)
    {
        if (!Directory.Exists(assetRoot))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(assetRoot, "Data"))
            && Directory.Exists(Path.Combine(assetRoot, "Maps"));
    }
}
