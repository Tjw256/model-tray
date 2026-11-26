using System.Text.Json;
namespace LocalQwenTray;
internal sealed record DiscoveryAnswer(int Status, string ContentType, string Body);
// Clients probe these read-only paths to learn what kind of server sits behind the endpoint; Hermes asks all of them
// whenever it opens a session, even one that never sends a message. Answering them must never load a model, so the
// gateway answers from what the engine itself said at its last load, per profile, kept on disk across tray restarts.
// A path the engine has never been asked gets llama-server's own answer for a path it doesn't have.
internal sealed class DiscoveryAnswers(string file)
{
    public static readonly string[] Paths = ["/api/v1/models", "/api/tags", "/v1/props", "/props", "/version"];
    // llama-server's reply to an unknown path (set_error_handler in tools/server/server-http.cpp).
    public static readonly DiscoveryAnswer NotFound = new(404, "application/json; charset=utf-8", "{\"error\":{\"message\":\"File Not Found\",\"type\":\"not_found_error\",\"code\":404}}");
    readonly object sync = new();
    Dictionary<string, Dictionary<string, DiscoveryAnswer>>? byProfile;

    public DiscoveryAnswer Get(string profile, string path)
    {
        lock (sync) return Loaded().TryGetValue(profile, out var answers) && answers?.TryGetValue(path, out var answer) == true && answer is not null ? answer : NotFound;
    }
    // Paths missing from a fresh capture (the engine was unreachable for them) keep their previous answer.
    public void Remember(string profile, IReadOnlyDictionary<string, DiscoveryAnswer> fresh)
    {
        lock (sync)
        {
            var all = Loaded();
            if (!all.TryGetValue(profile, out var answers) || answers is null) all[profile] = answers = [];
            foreach (var (path, answer) in fresh) answers[path] = answer;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, file, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still served from memory until the tray restarts */ }
        }
    }
    Dictionary<string, Dictionary<string, DiscoveryAnswer>> Loaded()
    {
        if (byProfile is not null) return byProfile;
        try { byProfile = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DiscoveryAnswer>>>(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { byProfile = null; }
        return byProfile ??= [];
    }
}
