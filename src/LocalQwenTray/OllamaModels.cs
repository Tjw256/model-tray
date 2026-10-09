using System.Text.Json;
namespace LocalQwenTray;
internal sealed record ModelFiles(string Variant, string Model, string? Projector, long ModelBytes);
// Reads Ollama's own manifests so llama-server loads Ollama's blobs in place (nothing copied or converted).
// A variant appears in the tray as soon as `ollama pull orcarouter/Qwen3.8-27B-Uncensored:<variant>` finishes.
internal static class OllamaModels
{
    public static string Repository { get; set; } = "orcarouter/Qwen3.8-27B-Uncensored";   // from config.json OllamaRepository
    public static string Root => Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } custom
        ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    public static ModelFiles? Find(string variant, string? root = null)
    {
        root ??= Root;
        var manifest = Path.Combine([root, "manifests", "registry.ollama.ai", .. Repository.Split('/'), variant]);
        if (!File.Exists(manifest)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            string? Blob(string mediaType) => doc.RootElement.GetProperty("layers").EnumerateArray()
                .Where(l => l.GetProperty("mediaType").GetString() == mediaType)
                .Select(l => Path.Combine(root, "blobs", l.GetProperty("digest").GetString()!.Replace(':', '-')))
                .FirstOrDefault(File.Exists);
            var model = Blob("application/vnd.ollama.image.model");
            if (model is null) return null;
            return new(variant, model, Blob("application/vnd.ollama.image.projector"), new FileInfo(model).Length);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    public static bool Installed(string variant) => Find(variant) is not null;
    // Every downloaded tag of the configured repository (e.g. q5_K_M, q4_K_M, q6_K).
    public static IReadOnlyList<string> Tags()
    {
        var dir = Path.Combine([Root, "manifests", "registry.ollama.ai", .. Repository.Split('/')]);
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Where(Installed).Order().ToArray() : []; }
        catch (IOException) { return []; }
    }
    public static string Describe(string variant) => variant switch
    {
        "q5_K_M" => "Q5_K_M — best quality",
        "q4_K_M" => "Q4_K_M — lighter, a bit faster",
        _ => variant,
    };
}
