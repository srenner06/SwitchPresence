namespace SwitchPresence;

/// <summary>
/// Build identity. The release workflow rewrites VersionTag from the git tag before publishing;
/// a local build keeps "development", which is what the f-API should see from one.
/// </summary>
public static class BuildInfo
{
    public const string VersionTag = "development";

    public const string RepositoryUrl = "https://github.com/srenner06/SwitchPresence";
}
