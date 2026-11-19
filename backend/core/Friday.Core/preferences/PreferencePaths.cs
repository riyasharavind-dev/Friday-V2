using System;
using System.IO;

namespace Friday.Core.Preferences;

public static class PreferencePaths
{
    public static string GetDatabasePath()
    {
        string? configured =
            Environment.GetEnvironmentVariable("FRIDAY_DATA_DIRECTORY");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(
                Path.GetFullPath(configured),
                "database",
                "preferences.db");
        }

        string? root = FindProjectRoot(AppContext.BaseDirectory)
            ?? FindProjectRoot(Directory.GetCurrentDirectory());

        string dataRoot = root is not null
            ? Path.Combine(root, "data")
            : Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "FRIDAY",
                "data");

        return Path.Combine(dataRoot, "database", "preferences.db");
    }

    private static string? FindProjectRoot(string start)
    {
        DirectoryInfo? directory = new(start);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "backend")) &&
                Directory.Exists(Path.Combine(directory.FullName, "frontend")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
