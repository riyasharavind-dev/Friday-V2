using System.Text.Json;
using Friday.Contracts;

namespace Friday.Desktop.Navigation;

// The model chooses an action through these tools. Execution still happens
// inside FRIDAY's validated navigation executor, never on the provider side.
public static class DesktopTools
{
    public static IReadOnlyList<AiToolDefinition> Definitions { get; } =
    [
        new(
            "open_app",
            "Open an installed Windows application or a Windows Settings page.",
            """
            {
              "type": "object",
              "properties": {
                "app": {
                  "type": "string",
                  "description": "Installed application name or Windows Settings destination, for example Notepad, Google Chrome, Wi-Fi settings, Display settings."
                }
              },
              "required": ["app"],
              "additionalProperties": false
            }
            """),
        new(
            "list_installed_apps",
            "List installed applications and Windows Settings destinations that FRIDAY can open.",
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """),
        new(
            "control_window",
            "Close, minimize, maximize or restore an application window.",
            """
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["close", "minimize", "maximize", "restore"]
                },
                "app": {
                  "type": "string",
                  "description": "Optional application name. Omit to act on the window that was active when the user spoke."
                }
              },
              "required": ["action"],
              "additionalProperties": false
            }
            """),
        new(
            "browser_navigation",
            "Switch browser tabs or navigate back and forward in the focused browser.",
            """
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["next_tab", "previous_tab", "back", "forward"]
                }
              },
              "required": ["action"],
              "additionalProperties": false
            }
            """)
    ];

    public static string? ToPhrase(string name, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string Value(string key) =>
                root.TryGetProperty(key, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String
                    ? element.GetString()?.Trim() ?? ""
                    : "";

            switch (name)
            {
                case "open_app":
                {
                    string app = Value("app");
                    return app.Length == 0 ? null : "open " + app;
                }
                case "control_window":
                {
                    string action = Value("action").ToLowerInvariant();
                    if (action is not ("close" or "minimize" or "maximize" or "restore"))
                        return null;

                    string app = Value("app");
                    if (app.Length > 0) return action + " " + app;

                    return action switch
                    {
                        "close" => "close this window",
                        "minimize" => "minimize this window",
                        "maximize" => "maximize this window",
                        _ => "restore this window"
                    };
                }
                case "browser_navigation":
                {
                    return Value("action").ToLowerInvariant() switch
                    {
                        "next_tab" => "next tab",
                        "previous_tab" => "previous tab",
                        "back" => "go back",
                        "forward" => "go forward",
                        _ => null
                    };
                }
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
