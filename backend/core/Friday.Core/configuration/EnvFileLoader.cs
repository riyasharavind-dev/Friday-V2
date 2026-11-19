using System;
using System.IO;
using System.Text;

namespace Friday.Core.Configuration;

public static class EnvFileLoader
{
    public static bool Load()
    {
        // Search from the application's location first.
        string? envPath = FindEnvFile(AppContext.BaseDirectory);

        // Also support running from the project/workspace terminal.
        envPath ??= FindEnvFile(Directory.GetCurrentDirectory());

        if (envPath is null)
        {
            return false;
        }

        foreach (string rawLine in File.ReadLines(envPath, Encoding.UTF8))
        {
            string line = rawLine.Trim();

            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (!IsValidName(name))
            {
                continue;
            }

            // Support values wrapped in matching single/double quotes.
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            // This app's .env takes precedence over terminal values.
            Environment.SetEnvironmentVariable(
                name,
                value,
                EnvironmentVariableTarget.Process);
        }

        return true;
    }

    private static string? FindEnvFile(string startPath)
    {
        DirectoryInfo? directory = new DirectoryInfo(startPath);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, ".env");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 ||
            !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}