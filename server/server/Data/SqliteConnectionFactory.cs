using Microsoft.Data.Sqlite;

namespace server.Data;

public class SqliteConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Must be set per-connection; not persisted in the DB file.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        return connection;
    }
}
