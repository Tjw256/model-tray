namespace LocalQwenTray;
// What to load: quant variant (Ollama tag) and preferred context. Chosen in the tray, saved in settings.json.
internal sealed record LaunchChoice(string Variant, int Context)
{
    public static readonly LaunchChoice Default = new(Policy.DefaultVariant, Policy.Ctx128);
}
internal static class Policy
{
    // One stable model name for clients, whichever quant is loaded (the tray shows the actual quant).
    public static string Model { get; set; } = "qwen3.8-27b-uncensored-q5_k_m";   // from config.json ModelName
    // Clients always talk to the tray's gateway on PublicPort; llama-server is private on BackendPort and is
    // loaded on the first request, then unloaded after the idle timeout (owner decision 2026-10-08).
    public const int PublicPort = 8000, BackendPort = 18081;
    public const int DefaultIdleMinutes = 5;
    public static readonly int?[] IdleChoices = [1, 5, 15, 30, 60, null];
    // 256K is the model's trained context (n_ctx_train 262144); going beyond needs YaRN and degrades recall.
    public const int Ctx128 = 131072, Ctx256 = 262144;
    public static readonly int[] ContextChoices = [Ctx128, Ctx256];
    public const int ContextCap = Ctx256, MinimumContext = 65536;
    public const string DefaultVariant = "q5_K_M";
    // Qwen3.8 thinking effort (chat template kwarg). Thinking text is the slow part: MTP accepts ~30% of drafted
    // tokens there vs 70-90% in answers; "low" measured 10-20% faster than the template default "xhigh" (2026-10-09).
    public static readonly string[] ReasoningChoices = ["low", "medium", "xhigh"];
    public const string DefaultReasoning = "medium";   // owner choice 2026-10-09: balance of speed and thoroughness
    public static readonly string[] Variants = ["q5_K_M", "q4_K_M"];
    // Whole-engine VRAM for Q5_K_M with the vision projector: 128K measured 25,042 MiB (2026-10-08); 256K measured
    // 28,531 MiB (2026-10-09, 17.6 s load, 157.5 tok/s, no spill). A lighter quant
    // needs less by roughly its smaller weights file.
    public const int Q5Need128 = 25042, Q5Need256 = 28531, MarginMiB = 400, HeadroomMiB = 512;
    // Weights-file size of the reference quant the needs were measured with (Q5_K_M, 18,631 MiB).
    public const int ReferenceWeightsMiB = 18631;
    // These numbers are specific to Qwen3.8 27B on a 32 GB card: re-measure them for another model or GPU.
    // Do NOT set NVIDIA "CUDA - Sysmem Fallback Policy = Prefer No Sysmem Fallback" for llama-server.exe: with the
    // model resident it made the whole desktop (mouse, typing, both monitors) lag by seconds; reverting it to
    // Driver Default fixed that (2026-10-08). With fallback allowed, a load that does not fit would silently spill
    // into system RAM, so loads are checked up front instead. CUDA does not evict other apps' VRAM.
    public static int Need(int context, int weightSavingMiB) => (context == Ctx256 ? Q5Need256 : Q5Need128) - weightSavingMiB + MarginMiB;
    public static int MinimumBudgetMiB => Need(Ctx128, 0);
    public static int Budget(GpuMemory memory)
    {
        if (memory.TotalMiB <= 0 || memory.FreeMiB < 0 || memory.FreeMiB > memory.TotalMiB) throw new InvalidOperationException("Invalid NVIDIA GPU memory snapshot");
        return memory.FreeMiB - HeadroomMiB;
    }
    // Preferred context if it fits, otherwise 128K; refuse (never spill to RAM) when even 128K does not fit.
    public static int ChooseContext(int budget, int preferred, int weightSavingMiB = 0)
    {
        if (preferred == Ctx256 && budget >= Need(Ctx256, weightSavingMiB)) return Ctx256;
        if (budget >= Need(Ctx128, weightSavingMiB)) return Ctx128;
        throw new InvalidOperationException($"Insufficient free VRAM: {budget + HeadroomMiB} MiB free; need {Need(Ctx128, weightSavingMiB) + HeadroomMiB} MiB for the GPU-only runtime with a {Ctx128 / 1024}K context. CPU offload and MTP downgrade are disabled. Close GPU games or unload Ollama models yourself, then retry. Nothing was automatically closed.");
    }
    public static string ContextLabel(int tokens) => $"{tokens / 1024}K";
    public static string Classify(bool running,bool ready,bool mismatch,int exitCode) => mismatch ? "Error: model identity mismatch; expected " + Model : running ? (ready ? "Ready" : "Loading model") : exitCode != 0 ? $"Error: native process exited {exitCode}; check logs" : "Stopped";
}
