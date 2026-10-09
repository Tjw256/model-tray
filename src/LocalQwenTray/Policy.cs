namespace LocalQwenTray;
// What to load: quant variant (Ollama tag), preferred context and parallel request slots. Chosen in the tray, saved in settings.json.
internal sealed record LaunchChoice(string Variant, int Context, int Slots = Policy.DefaultSlots)
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
    public const int Ctx64 = 65536, Ctx96 = 98304, Ctx128 = 131072, Ctx256 = 262144;
    public static readonly int[] ContextChoices = [Ctx128, Ctx256];
    // When the chosen context does not fit (e.g. a video editor holds VRAM), the largest smaller one that fits is used.
    public static readonly int[] FallbackContexts = [Ctx256, Ctx128, Ctx96, Ctx64];
    public const int ContextCap = Ctx256, MinimumContext = Ctx64;
    // Two slots share one KV pool (--kv-unified; each can still use the whole context). A short request no longer
    // waits behind a long one: 12.7 s -> 1.2 s while a cached background conversation generated, which ran 7% slower
    // (2026-10-09). The second slot costs ~0.8 GiB (measured 777 MiB).
    public const int DefaultSlots = 2, SlotOverheadMiB = 800;
    public static readonly int[] SlotChoices = [2, 1];
    public const string DefaultVariant = "q5_K_M";
    // Qwen3.8 thinking effort (chat template kwarg). Thinking text is the slow part: MTP accepts ~30% of drafted
    // tokens there vs 70-90% in answers; "low" measured 10-20% faster than the template default "xhigh" (2026-10-09).
    public static readonly string[] ReasoningChoices = ["low", "medium", "xhigh"];
    public const string DefaultReasoning = "medium";   // owner choice 2026-10-09: balance of speed and thoroughness
    public static readonly string[] Variants = ["q5_K_M", "q4_K_M"];
    // Whole-engine VRAM for Q5_K_M with the vision projector: 128K measured 25,042 MiB (2026-10-08); 256K measured
    // 28,531 MiB (2026-10-09, 17.6 s load, 157.5 tok/s, no spill). Below 128K the KV cache shrinks by ~22 MiB per
    // 1K tokens; 20 is used so smaller contexts are never over-promised. A lighter quant needs less by roughly its
    // smaller weights file.
    public const int Q5Need128 = 25042, Q5Need256 = 28531, MiBPer1KBelow128 = 20, MarginMiB = 400, HeadroomMiB = 512;
    // Weights-file size of the reference quant the needs were measured with (Q5_K_M, 18,631 MiB).
    public const int ReferenceWeightsMiB = 18631;
    // These numbers are specific to Qwen3.8 27B on a 32 GB card: re-measure them for another model or GPU.
    // Do NOT set NVIDIA "CUDA - Sysmem Fallback Policy = Prefer No Sysmem Fallback" for llama-server.exe: with the
    // model resident it made the whole desktop (mouse, typing, both monitors) lag by seconds; reverting it to
    // Driver Default fixed that (2026-10-08). With fallback allowed, a load that does not fit would silently spill
    // into system RAM, so loads are checked up front instead. CUDA does not evict other apps' VRAM.
    public static int Need(int context, int weightSavingMiB, int slots = 1)
    {
        int engine = context >= Ctx256 ? Q5Need256 : context >= Ctx128 ? Q5Need128 : Q5Need128 - (Ctx128 - context) / 1024 * MiBPer1KBelow128;
        return engine - weightSavingMiB + MarginMiB + (Math.Max(1, slots) - 1) * SlotOverheadMiB;
    }
    public static int MinimumBudgetMiB => Need(Ctx64, 0);
    public static int Budget(GpuMemory memory)
    {
        if (memory.TotalMiB <= 0 || memory.FreeMiB < 0 || memory.FreeMiB > memory.TotalMiB) throw new InvalidOperationException("Invalid NVIDIA GPU memory snapshot");
        return memory.FreeMiB - HeadroomMiB;
    }
    // Largest context (up to the preferred one) that fits, keeping two slots where possible; refuse (never spill to
    // system RAM) when even 64K with one slot does not fit.
    public static (int Context, int Slots) ChooseLaunch(int budget, int preferredContext, int preferredSlots, int weightSavingMiB = 0)
    {
        foreach (var context in FallbackContexts.Where(c => c <= preferredContext))
            foreach (var slots in preferredSlots > 1 ? new[] { preferredSlots, 1 } : new[] { 1 })
                if (budget >= Need(context, weightSavingMiB, slots)) return (context, slots);
        throw new InvalidOperationException($"Insufficient free VRAM: {budget + HeadroomMiB} MiB free; need {Need(Ctx64, weightSavingMiB) + HeadroomMiB} MiB for the GPU-only runtime with a {Ctx64 / 1024}K context. CPU offload and MTP downgrade are disabled. Close GPU-heavy apps (games, video editors) or unload Ollama models yourself, then retry. Nothing was automatically closed.");
    }
    public static int ChooseContext(int budget, int preferred, int weightSavingMiB = 0) => ChooseLaunch(budget, preferred, 1, weightSavingMiB).Context;
    public static string ContextLabel(int tokens) => $"{tokens / 1024}K";
    public static string Classify(bool running,bool ready,bool mismatch,int exitCode) => mismatch ? "Error: model identity mismatch; expected " + Model : running ? (ready ? "Ready" : "Loading model") : exitCode != 0 ? $"Error: native process exited {exitCode}; check logs" : "Stopped";
}
