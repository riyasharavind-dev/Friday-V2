using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Friday.Core.Storage.Interfaces;

namespace Friday.Core.Storage.Sqlite;

public sealed class SqlitePreferenceStore : IPreferenceStore
{
    private static readonly string[] SupportedKeys =
    {
        "preferred_address",
        "default_language",
        "response_length",
        "speech_rate"
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqlitePreferenceStore(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS user_preferences (
                    preference_key TEXT PRIMARY KEY,
                    preference_value TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS preference_schema (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );

                INSERT OR IGNORE INTO preference_schema(version, applied_utc)
                VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();

            command.CommandText =
                "SELECT preference_key, preference_value FROM user_preferences;";

            using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            var values = new Dictionary<string, string>();

            while (await reader.ReadAsync(cancellationToken))
                values[reader.GetString(0)] = reader.GetString(1);

            return values;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            foreach (string key in SupportedKeys)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.Parameters.AddWithValue("$key", key);

                if (values.TryGetValue(key, out string? value))
                {
                    command.CommandText =
                        """
                        INSERT INTO user_preferences
                            (preference_key, preference_value, updated_utc)
                        VALUES
                            ($key, $value, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                        ON CONFLICT(preference_key) DO UPDATE SET
                            preference_value = excluded.preference_value,
                            updated_utc = excluded.updated_utc
                        WHERE user_preferences.preference_value
                            <> excluded.preference_value;
                        """;

                    command.Parameters.AddWithValue("$value", value);
                }
                else
                {
                    command.CommandText =
                        "DELETE FROM user_preferences WHERE preference_key = $key;";
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }
}
