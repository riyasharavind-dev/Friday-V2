using Friday.Core.Navigation;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Friday.Desktop.Navigation;

public sealed class WindowsAppCatalog : IInstalledAppCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<InstalledApp>? _cache;

    public async Task<IReadOnlyList<InstalledApp>> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // The speech grammar uses this same session snapshot. Never put a slow
            // Get-StartApps scan into a time-sensitive command after an idle timeout.
            // Restart or an explicit refresh discovers newly installed apps.
            if (!refresh && _cache is not null)
                return _cache;
            var apps = await Task.Run(() => DiscoverDesktopApps(cancellationToken), cancellationToken);
            AddExecutionAliases(apps);
            // Compile the speech grammar from a complete initial snapshot, not a stale
            // snapshot taken before packaged Windows apps finish discovery.
            await AddStartAppsAsync(apps, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Prefer a concrete shortcut/executable when the same display name occurs twice.
            _cache = apps.OrderBy(a => a.IsAppId)
                .GroupBy(a => NavigationParser.Normalize(a.Name), StringComparer.Ordinal)
                .Select(g => g.First()).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return _cache;
        }
        finally { _gate.Release(); }
    }

    private static void AddExecutionAliases(List<InstalledApp> apps)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly))
                Add(apps, Path.GetFileNameWithoutExtension(path), path, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static List<InstalledApp> DiscoverDesktopApps(CancellationToken token)
    {
        var apps = new List<InstalledApp>();
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    token.ThrowIfCancellationRequested();
                    Add(apps, Path.GetFileNameWithoutExtension(shortcut), shortcut, false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var paths = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (paths is null) continue;
                foreach (var name in paths.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    using var entry = paths.OpenSubKey(name);
                    if (entry?.GetValue(null) is not string value) continue;
                    string target = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (!Path.IsPathFullyQualified(target) || !File.Exists(target)
                        || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string display = FileVersionInfo.GetVersionInfo(target).FileDescription ?? "";
                    Add(apps, string.IsNullOrWhiteSpace(display) ? Path.GetFileNameWithoutExtension(target) : display,
                        target, false);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return apps;
    }

    private static async Task AddStartAppsAsync(List<InstalledApp> apps, CancellationToken cancellationToken)
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(executable)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        // Fixed, read-only Windows discovery. No user/model text is interpolated.
        process.StartInfo.ArgumentList.Add(
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress");
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string json = await output;
            await errors;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return;
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray() : new[] { document.RootElement };
            foreach (var item in items)
            {
                if (!item.TryGetProperty("Name", out var name) || !item.TryGetProperty("AppID", out var id)) continue;
                string appId = id.GetString() ?? "";
                if (appId.Length is < 1 or > 512 || appId.Any(char.IsControl)
                    || appId.Contains('"') || appId.Contains('\\') || appId.Contains('/')) continue;
                Add(apps, name.GetString() ?? "", appId, true);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is JsonException or IOException or System.ComponentModel.Win32Exception) { }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void Add(List<InstalledApp> apps, string name, string target, bool isAppId)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        string normal = NavigationParser.Normalize(name);
        var words = normal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w is "uninstall" or "uninstaller" or "setup" or "installer")) return;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (isAppId ? "app:" : "file:") + target.ToUpperInvariant())))[..24].ToLowerInvariant();
        apps.Add(new InstalledApp(id, name, target, isAppId));
    }
}
