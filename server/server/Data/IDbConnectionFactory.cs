using Microsoft.Data.Sqlite;

namespace server.Data;

public interface IDbConnectionFactory
{
    SqliteConnection CreateConnection();
}
