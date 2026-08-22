namespace MMMAtlas.Configuration;

public enum AtlasBuildMode
{
    Development,
    Test,
    Production
}

public sealed record AtlasBuildPolicy
{
    private AtlasBuildPolicy(AtlasBuildMode mode)
    {
        Mode = mode;
    }

    public static AtlasBuildPolicy Current { get; } = For(CurrentBuildMode());

    public AtlasBuildMode Mode { get; }

    public bool DebugEnabled => Mode == AtlasBuildMode.Development;

    public bool UsesDevelopmentDatabase => Mode != AtlasBuildMode.Production;

    public bool TestFeaturesEnabled => Mode != AtlasBuildMode.Production;

    public string DatabaseConnectionName => UsesDevelopmentDatabase
        ? "MMMAtlasDevelopment"
        : "MMMAtlasProduction";

    public static AtlasBuildPolicy For(AtlasBuildMode mode) => new(mode);

    private static AtlasBuildMode CurrentBuildMode()
    {
#if ATLAS_BUILD_DEVELOPMENT
        return AtlasBuildMode.Development;
#elif ATLAS_BUILD_TEST
        return AtlasBuildMode.Test;
#elif ATLAS_BUILD_PRODUCTION
        return AtlasBuildMode.Production;
#else
#error The MMM Atlas build mode has not been configured.
#endif
    }
}
