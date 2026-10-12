using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MMMAtlas.Steam;

public sealed class SteamLibraryFilterOptions
{
    public bool RequireGameType { get; set; } = true;
    public bool ExcludeBeta { get; set; } = true;
}

public sealed class SteamLibraryFilter(IOptionsMonitor<SteamLibraryFilterOptions> options)
{
    // IS NOT retains unknown classifications (NULL) as well as confirmed false values.
    public const string VisibleSql = """
        (($requireGameType = 0 OR steam.hasParentApp IS NOT 1)
        AND ($excludeBeta = 0 OR steam.isBeta IS NOT 1))
        """;

    public SteamLibraryFilterOptions Settings => options.CurrentValue;

    public static void AddParameters(SqliteCommand command, SteamLibraryFilterOptions? settings = null)
    {
        settings ??= new SteamLibraryFilterOptions();
        command.Parameters.AddWithValue("$requireGameType", settings.RequireGameType ? 1 : 0);
        command.Parameters.AddWithValue("$excludeBeta", settings.ExcludeBeta ? 1 : 0);
    }
}
