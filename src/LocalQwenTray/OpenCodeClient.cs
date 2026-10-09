using System.Text.Json;
using System.Text.Json.Nodes;
namespace LocalQwenTray;
// Registers the tray's endpoint as a provider in OpenCode's global config, so OpenCode (and T3 Code's OpenCode
// driver, which runs OpenCode) can use Local Qwen. Only the "local-qwen" provider entry is written; everything else
// in opencode.json is kept as is. A file that is not plain JSON (e.g. has comments) is never rewritten.
internal static class OpenCodeClient
{
    public const string ProviderId = "local-qwen";
    public const int OutputLimit = 32768;
    public static string Endpoint => $"http://127.0.0.1:{Policy.PublicPort}/v1";
    public static string ConfigPath => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
        ? Path.Combine(xdg, "opencode", "opencode.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "opencode.json");
    public static bool Installed()
    {
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "opencode.cmd");
        if (File.Exists(npm)) return true;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), "opencode.exe")) || File.Exists(Path.Combine(dir.Trim(), "opencode.cmd")));
    }
    static JsonObject? Read(string path)
    {
        if (!File.Exists(path)) return new JsonObject { ["$schema"] = "https://opencode.ai/config.json" };
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }   // comments / JSONC: leave the user's file alone
    }
    public static bool IsConnected(string? path = null)
    {
        var root = Read(path ?? ConfigPath);
        return root?["provider"]?[ProviderId]?["options"]?["baseURL"]?.GetValue<string>() == Endpoint;
    }
    // Adds or refreshes the provider. Returns null on success, or a reason it was not written.
    public static string? Connect(string apiKey, int context, string? path = null)
    {
        path ??= ConfigPath;
        var root = Read(path);
        if (root is null) return $"{path} is not plain JSON (comments?), so it was not changed. Add the \"{ProviderId}\" provider by hand (see README).";
        var providers = root["provider"] as JsonObject ?? (JsonObject)(root["provider"] = new JsonObject());
        providers[ProviderId] = new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = "Local Qwen (tray)",
            ["options"] = new JsonObject { ["baseURL"] = Endpoint, ["apiKey"] = apiKey },
            ["models"] = new JsonObject
            {
                [Policy.Model] = new JsonObject
                {
                    ["name"] = "Qwen3.8 27B (Local Qwen)",
                    ["attachment"] = true, ["reasoning"] = true, ["tool_call"] = true, ["temperature"] = true,
                    ["modalities"] = new JsonObject { ["input"] = new JsonArray("text", "image"), ["output"] = new JsonArray("text") },
                    ["limit"] = new JsonObject { ["context"] = context, ["output"] = OutputLimit },
                },
            },
        };
        Write(path, root);
        return null;
    }
    // Keeps OpenCode's context limit equal to what the engine actually loaded (fallbacks can lower it).
    public static void UpdateContext(int context, string? path = null)
    {
        path ??= ConfigPath;
        var root = Read(path);
        if (root?["provider"]?[ProviderId]?["models"]?[Policy.Model]?["limit"] is not JsonObject limit) return;
        if (limit["context"]?.GetValue<int>() == context) return;
        limit["context"] = context;
        Write(path, root);
    }
    static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backup = path + ".before-local-qwen";
        if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }
}
