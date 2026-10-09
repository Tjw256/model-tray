# Model Tray — fast local Qwen on Windows

A small Windows tray app that runs a local LLM with **[llama.cpp](https://github.com/ggml-org/llama.cpp)** directly, behind an OpenAI-compatible endpoint, and loads the model **only when a request arrives**, like Ollama does. It unloads the model again when idle.

On the same model file and GPU it generated **about 2.8× faster than Ollama**:

| Same Qwen3.8 27B Q5_K_M GGUF, RTX 5090, one request, 512 tokens, temperature 0 | Generation speed |
|---|---:|
| Ollama 0.35.0 | 60 tok/s |
| llama.cpp, plain | 62 tok/s |
| **Model Tray (llama.cpp + built-in MTP speculative decoding)** | **172–177 tok/s** |

On real multi-turn coding work (temperature 0.7, varied tasks, 25 min) it averaged a **median of 161 tok/s** (range 117–212). Speed depends on how predictable the text is and drops as a conversation grows: about 108 tok/s with 118K tokens already in context, and 84 tok/s at 252K.

It is **Windows-first** and currently **tuned for Qwen3.8 27B on a 32 GB NVIDIA card**. Everything model- or GPU-specific is in a few clearly marked places, so **feel free to fork it and adapt it** to your model and hardware (see [Customizing](#customizing)).

<p align="center"><img src="docs/panel-sleeping.png" width="300"> &nbsp; <img src="docs/panel-ready.png" width="300"></p>

## Why it's faster

Ollama and plain llama.cpp ran at about the same speed. The gain comes from settings that this app turns on for you:

- **MTP speculative decoding.** Qwen3.8 ships with a built-in *multi-token prediction* head. llama.cpp's `--spec-type draft-mtp` uses it to draft 5 tokens ahead, and the main model checks them in one pass. No separate draft model is needed. A draft depth of 5 measured fastest; 4, 6 and 7 were all slower.
- **CUDA graphs stay on.** Disabling them (`GGML_CUDA_DISABLE_GRAPHS=1`, a common "stability fix") cost 28% (174 → 125 tok/s) in an isolated A/B test.
- **Everything on the GPU**, including the token-embedding table that llama.cpp otherwise leaves in system RAM, with flash attention and a 4-bit KV cache.
- **No memory-mapping.** `--load-mode none` means the 18.6 GB weights file doesn't also sit in system RAM after it has been copied to VRAM. With the default mmap, idle RAM use was 19 GB; with this setting it is 0.8 GB.

## How it works

```
 your apps (any OpenAI-compatible client)
        │  http://127.0.0.1:8000/v1  + API key
        ▼
 ┌─────────────────────────────── Model Tray (starts with Windows) ───────────────────────────────┐
 │ gateway on :8000 ── first request? ──► start llama-server, wait until ready (~20 s), then stream │
 │                   ── /v1/models, /health ──► answered without loading anything                  │
 │                   ── idle for N minutes ──► stop llama-server, VRAM freed                       │
 └──────────────────────────────────────────────┬──────────────────────────────────────────────────┘
                                                ▼
                         llama-server on private 127.0.0.1:18081
                         (loads the model files Ollama already downloaded)
```

- **Always listening, loaded on demand.** The tray starts at login and holds port 8000. The first real request starts `llama-server`; the request waits about 20–25 s for the load and then streams normally. After the idle timeout (default 5 min; 1/5/15/30/60 min or never) the model is unloaded. Model-list and health requests never wake the GPU, and several first requests at once share a single load.
- **Uses Ollama's downloads in place.** It reads Ollama's manifests and loads the GGUF blobs directly, so nothing is copied or converted and Ollama keeps working. Every downloaded tag of the configured model (e.g. `q5_K_M`, `q4_K_M`) appears as a choice in the tray.
- **Vision.** If the Ollama model includes a vision projector, it is loaded with `--mmproj`, so image input works.
- **Context 128K or 256K.** 256K is the model's trained maximum. If there isn't enough free VRAM for 256K when the model loads, it falls back to 128K and the panel tells you why. It never spills into system RAM.
- **Checks before and after loading.** Free VRAM is checked first, and a load that doesn't fit is refused with a clear message (HTTP 503). After loading, the log is checked to confirm that all layers, the KV cache and the MTP draft are on the GPU.
- **Safe process ownership.** It only ever stops the `llama-server` it started, tracked by PID, start time and executable. Unknown programs on its ports are never killed.

### The tray

- **Icon dot:** grey = sleeping, amber = loading, green = ready, blue = generating, red = needs attention.
- **Left-click** opens the status panel (shown above). **Right-click** gives the same actions as a menu: Load/Unload now, Context, Model, Unload when idle, Start with Windows, Copy API endpoint, Copy API key, Open log, Open config folder, Quit.
- Quitting the tray also unloads the model, since clients can't reach it without the tray.

## Requirements

- Windows 10/11 x64 and an NVIDIA GPU. It was built and measured on an RTX 5090 (32 GB); the default model needs about 25 GB of VRAM at 128K context, or about 28.5 GB at 256K.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. The ASP.NET Core 10 runtime is needed to run it.
- A llama.cpp **Windows CUDA** build with MTP support. Tested with release [b11429](https://github.com/ggml-org/llama.cpp/releases/tag/b11429): `llama-b11429-bin-win-cuda-13.4-x64.zip`. You may also need `cudart-llama-bin-win-cuda-13.4-x64.zip` unless the CUDA 13 runtime is already installed.
- [Ollama](https://ollama.com), used only to download the model.

## Install

1. **Download the model** (no weights are included in this repo):
   ```
   ollama pull orcarouter/Qwen3.8-27B-Uncensored:q5_K_M    # best quality, ~20 GB
   ollama pull orcarouter/Qwen3.8-27B-Uncensored:q4_K_M    # lighter/faster, ~18 GB (optional)
   ```
   Model pages: [Ollama](https://ollama.com/orcarouter/Qwen3.8-27B-Uncensored) · [Hugging Face](https://huggingface.co/orcarouter/Qwen3.8-27B-Uncensored). This build is *abliterated* (refusals removed; its authors report standard benchmarks within ±1.3 points of the original Qwen3.8 27B). Any Qwen3.8 27B GGUF with its MTP head should work. See [Customizing](#customizing).
2. **Build** (from `src/LocalQwenTray`):
   ```
   dotnet publish -c Release -r win-x64 --self-contained false -p:UseAppHost=true -o publish
   ```
3. **Add llama.cpp**: unzip the llama.cpp build (plus the cudart zip if needed) into `publish\llama.cpp\`, so that `publish\llama.cpp\llama-server.exe` exists.
4. **Run** `publish\LocalQwenTray.exe`. On first run it creates `%LOCALAPPDATA%\LocalQwen\config.json` with a random API key.
5. **Point your client** at `http://127.0.0.1:8000/v1`. Use **Copy API key** in the tray menu for the key, and the model name `qwen3.8-27b-uncensored-q5_k_m`; any name works, since there is one model per server. Enable **Start with Windows** in the tray if you want it there from login.

## Configuration

`%LOCALAPPDATA%\LocalQwen\config.json` (open it with **Open config folder**; see `config.example.json`):

| Field | Meaning |
|---|---|
| `ApiKey` | Key clients send as `Authorization: Bearer …`. Random on first run. |
| `LlamaServer` | Path to `llama-server.exe`. Default: `<app folder>\llama.cpp\llama-server.exe`. |
| `ChatTemplate` | Optional Jinja template. Empty = the template embedded in the GGUF. The bundled `qwen38-codex-chat-template.jinja` merges multiple system messages, which some agent clients send. |
| `OllamaRepository` | Ollama model to load, e.g. `orcarouter/Qwen3.8-27B-Uncensored` (official library models are `library/<name>`). |
| `ModelName` | Name advertised on `/v1/models`. |
| `ClientSyncCommand` | Optional command run after a load whose context size changed. `{context}` is replaced with the token count. Use it to update your clients' context settings. |

Tray choices (context, model, idle timeout) are saved in `settings.json` in the same folder. Logs are in the same folder: `tray.log` and `native-engine.log`.

## Customizing

The defaults are deliberately specific. These are the places to change for another model or GPU:

- **`src/LocalQwenTray/Policy.cs`**: the VRAM needed at 128K/256K (`Q5Need128`, `Q5Need256`, measured on Qwen3.8 27B Q5_K_M with vision), the reference weights size, and the context choices. Re-measure these on your setup; the tray uses them to decide whether a load fits.
- **`src/LocalQwenTray/NativeHost.cs` → `BuildArguments`**: all llama-server flags, including MTP draft depth, KV-cache type, batch sizes and threads. For a model **without** an MTP head, remove the `--spec-*` flags and the MTP lines in `ValidateResidency`.
- **`config.json`**: a different Ollama model, a different llama.cpp build, or your own chat template.

Pull requests are welcome.

## Lessons from building it (Windows + NVIDIA)

- **Don't set "CUDA – Sysmem Fallback Policy: Prefer No Sysmem Fallback"** for `llama-server.exe` in the NVIDIA Control Panel. With a large model resident, it made the whole desktop lag by seconds: the mouse, typing, both monitors. Driver Default fixed it. This app checks VRAM before loading instead.
- **Crashes with "CUDA error: an illegal memory access" during long generations** can be the GPU, not llama.cpp. Check the Windows **System** log for `nvlddmkm` events. On the development machine they were caused by an undervolt curve that boosted to an unstable clock whenever the load dipped. Capping the curve fixed it, with no speed loss.
- **Prompt re-processing on hybrid models.** Qwen3.8 mixes attention and recurrent layers. Appending to a conversation is cheap, because only the new tokens are processed, but editing earlier history forces a full re-read.

## Tests

`python verify_all.py` (in `src/LocalQwenTray`) builds the app and runs the self-tests. These cover the VRAM policy, launch arguments, process ownership, a real Kestrel gateway against a fake llama-server (auth, on-demand load, live streaming, idle unload, load failures) and real WinForms UI tests. It then publishes and checks the published exe. None of the tests use the GPU.

`LocalQwenTray.exe --ui-preview <folder>` renders the status panel and icons to PNG files.

## License

MIT. See [LICENSE](LICENSE). Model weights and llama.cpp are not included and are covered by their own licenses.
