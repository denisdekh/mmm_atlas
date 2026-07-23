using Microsoft.Data.Sqlite;

namespace MMMAtlas.Data;

public sealed class DatabaseConnection
{
    private DatabaseConnection(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static DatabaseConnection Create(
        string connectionString,
        string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (!string.IsNullOrWhiteSpace(builder.DataSource)
            && builder.DataSource != ":memory:"
            && !Path.IsPathFullyQualified(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(
                builder.DataSource,
                contentRootPath);
        }

        var databaseDirectory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        return new DatabaseConnection(builder.ConnectionString);
    }
}
