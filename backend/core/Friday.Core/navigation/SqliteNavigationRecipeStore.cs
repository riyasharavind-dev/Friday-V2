using Microsoft.Data.Sqlite;

namespace Friday.Core.Navigation;

public sealed class SqliteNavigationRecipeStore(string databasePath) : INavigationRecipeStore
{
    private async Task<SqliteConnection> ConnectAsync(CancellationToken token)
    {
        string path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS navigation_recipes (
                    intent_key TEXT PRIMARY KEY,
                    target_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(token);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<NavigationRecipe?> ReadAsync(string key, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_id, display_name FROM navigation_recipes WHERE intent_key=$key";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task SaveAsync(string key, NavigationRecipe recipe, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO navigation_recipes(intent_key,target_id,display_name,updated_utc)
            VALUES($key,$target,$name,$time)
            ON CONFLICT(intent_key) DO UPDATE SET target_id=excluded.target_id,
            display_name=excluded.display_name, updated_utc=excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$target", recipe.TargetId);
        command.Parameters.AddWithValue("$name", recipe.DisplayName);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task RemoveAsync(string key, CancellationToken token)
    {
        await using var connection = await ConnectAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM navigation_recipes WHERE intent_key=$key";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(token);
    }
}
