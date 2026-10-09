# Local AMD Windows adaptation

Delivery target: local changes only. Tested with RX 9070 XT, Windows, llama.cpp Vulkan b11517 and the existing Qwen3.8 27B IQ2_XXS Ollama blob.

Use `GpuDevice: Vulkan0`, `GpuName: AMD Radeon RX 9070 XT`, `EnableVision: false`, and `SpeculativeDecoding: true` (MTP1 + ngram) or false (plain). Set IQ2_XXS, one slot and 65536 tokens in settings. `MODEL_TRAY_HOME` selects an isolated state directory.

The AMD profile uses batch 256, main and draft Q4 KV, no context checkpoints and one MTP draft token. GPU selection, Windows physical memory and GPU logs are validated. The GPU-memory estimator is calibrated for this hardware/model; it is not a general estimator for arbitrary models or AMD cards. The installed Hermes requires at least 64000 tokens, so the launcher refuses a smaller allocation rather than claiming a larger context.

CUDA defaults and the original tests remain available. New AMD regression tests cover adapter selection, physical free memory, shared-memory rejection, argument selection, mode-specific budgets and cache compatibility. Session cache fingerprints include the native executable SHA-256 and exact launch arguments.

Measured short 64K text test: plain 48.44 / 48.30 tokens/s; MTP1+ngram 51.94 / 27.36 tokens/s. Both produced a real Hermes response. No consistent speedup was demonstrated. Image input is disabled in this delivery. Long-context workload, agent file edits and extensive tool execution were not tested.
