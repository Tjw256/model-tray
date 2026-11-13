# Profiles and extraction integration

The gateway remains `http://127.0.0.1:8000/v1`. All profile and status endpoints
require the existing gateway API key, using `Authorization: Bearer <key>` or
`x-api-key`. `/health` keeps its existing unauthenticated behavior.

## Request routing

Use `model: "savannah-extraction"` in chat, Responses or native completion requests.
The gateway waits for every active response, including streamed responses, before
unloading a different profile. Requests arriving after a pending switch wait behind
it. Requests on the same profile can overlap, subject to that profile's slot count.
A disconnected waiting caller does not cancel other callers or active generations.

All other model names select the existing tray model and its adaptive VRAM policy.
The tray's context, quant, reasoning and slot choices continue to apply there. Model
listing always includes that stable model ID and configured profiles, without
switching or loading anything. Loading an extraction profile does not change
coding clients' synchronized context limits.

Named profiles forward the request body unchanged, including `chat_template_kwargs`,
reasoning fields, `max_tokens`, schemas and tools. The legacy route keeps its existing
merge behavior: it adds the tray effort unless the caller sets `reasoning_effort`
in kwargs or at the top level, and adds `preserve_thinking: false` unless the caller
sets that kwarg. Existing client values and `max_tokens` are preserved. Turning
thinking off for a named-profile retry needs no reload.

The same idle timeout chosen in the tray applies to all profiles. Savannah's old
standalone server had a separate 20-minute watchdog. It is not installed in Model
Tray. A request arriving during idle slot-save waits for unloading and reloads.
Named profiles do not save or restore implicit conversation caches.

## Exact Savannah configuration

Existing configs automatically get the built-in `savannah-extraction` profile unless
they already specify a `Profiles` dictionary. It reads this blob in place:

```text
%USERPROFILE%\.ollama\models\blobs\sha256-1a0d6fe2e48417f1571a57a7104b4d5da0c45c3d00de18fa7aa48affca5a24d7
```

It uses 32768 context, one slot, f16 K/V for target and draft, shared-weight
`draft-mtp` depth 5, probabilistic draft sampling, four target/draft threads,
batch/ubatch 512, Flash Attention, all GPU layers, GPU embeddings,
`--fit off --load-mode none --no-host --jinja --reasoning-format deepseek --slots`.
There is no custom template, n-gram speculation, host prompt-cache flag or checkpoint
flag. The template is the GGUF's embedded `tokenizer.chat_template`.

This reproduces the saved benchmark configuration except for loopback binding,
private backend port, API key, alias and log destination. It does not start Ollama.

## Future models

Add entries to `Profiles` in the private `config.json`, then restart the tray.
Each entry has `ModelFile`, optional `Projector` and `ChatTemplate`, `ContextTokens`,
`Slots`, `RequiredFreeMiB` and an `Arguments` string array. Paths should be absolute.
`Arguments` specifies model-specific KV, speculation, placement, batching and template
options. `ModelProfile.Savannah()` in `Profiles.cs` is the complete extraction preset.

Model paths, context, slots, binding, authentication and template files are managed
by the tray and cannot be overridden through `Arguments`. Automatic fitting is
disabled. External draft models/adapters and model-download/router options are
rejected because their assets are not yet covered by the fingerprint.

`RequiredFreeMiB` is a preflight floor, not a measurement of an unknown model.
Re-measure it for a new model. Profiles never shrink to fit. GPU-only placement
is checked from startup logs; MTP evidence is required only when MTP is configured.
Missing files, insufficient VRAM, invalid flags and residency failures return 503.

## Status

`GET /v1/status` returns, for example:

```json
{
  "loaded_profile": "savannah-extraction",
  "state": "busy",
  "busy": true,
  "in_flight": 1,
  "free_vram_mib": 5800
}
```

`loaded_profile` is `null` when unloaded and `"default"` for the legacy tray model.
`state` is `sleeping`, `loading`, `unloading`, `ready`, `busy` or `error`. `in_flight` includes
requests waiting for admission/loading. `free_vram_mib: null` means the GPU poll
failed, never zero free memory. Status and profile listing never load the GPU.

## Fingerprints

`GET /v1/profiles` lists each exact named profile and its fingerprint URL.
`GET /v1/profiles/{name}/fingerprint` returns:

```json
{
  "schema_version": 1,
  "profile": "savannah-extraction",
  "sha256": "<digest of the canonical manifest>",
  "manifest": {
    "schema_version": 1,
    "profile": "savannah-extraction",
    "gguf_sha256": "<full GGUF digest>",
    "projector_sha256": null,
    "llama_build": {"llama-server.exe": "<digest>", "ggml-cuda.dll": "<digest>"},
    "launch_args": ["<every actual launch argument, excluding credentials>"],
    "input_files": {"<additional input file path>": "<digest>"},
    "template_sha256": "<UTF-8 template digest>",
    "environment_sha256": "<digest of inherited GGML_/CUDA_ settings>",
    "request_policy": "passthrough"
  }
}
```

The build map includes the executable and every adjacent runtime DLL, sorted by
filename. Inherited `LLAMA_*` settings are cleared and CUDA graphs are enabled.
Authentication is injected through the child environment and is never fingerprinted.
All arguments, including non-output paths and transport flags, are covered. Moving
the log destination can therefore change the fingerprint too.

The fingerprint is captured before each exact-profile launch and saved with the
owned process receipt. While that process is alive the endpoint returns its captured
fingerprint, even if files have since changed on disk. While unloaded it hashes the
configured files without launching anything. Full GGUF hashing can take seconds.
A missing/unsupported template or unreadable asset returns 503, never an invented
digest. The adaptive legacy route is not an exact named profile.

Savannah should save `schema_version`, `profile` and `sha256` with each import and
keep the manifest as benchmark evidence. Compare fingerprints before and after a
batch; require a fresh benchmark after a change. Per-call prompt/sampling/thinking
settings remain the caller's responsibility and belong in its benchmark record.

## Key access and remote benchmarks

`Savannah.Worker` on this PC should read `ApiKey` from
`%LOCALAPPDATA%\LocalQwen\config.json` under the Windows account running Model Tray,
or receive it through its existing private credential configuration. Do not log it
or put it in a repository. The key can also be copied from the tray menu.

The separate `Savannah_Bench_Llama_Key` in SkillBase authenticates the existing
standalone `:8001` benchmark server. It is not automatically the Model Tray key.
Keep remote benchmarks on that server until T approves tailnet exposure and the
remote credential handoff. This change adds no listener or firewall rule on the
tailnet and does not edit Savannah's application configuration.

## Verification

Run `python verify_all.py` from `src/LocalQwenTray` for non-GPU regression checks.
These include profile draining, cancellation, thinking passthrough, model listing,
status auth, fingerprint invalidation and the idle-save race.

After the GPU reservation has been released, and only with exclusive test access:

```powershell
dotnet bin/Release/net10.0-windows/LocalQwenTray.dll --verify-profile <new-empty-output-folder>
```

This explicit mode uses the installed config's binary and key, checks compute
processes and backend-port availability before each load, then runs an independent
direct baseline and the gateway profile sequentially. It compares generated token
IDs for a fixed synthetic prompt at temperature 0, seed 42, with prompt reuse off.
It records both responses, backend decode timings, logs, arguments and fingerprint.
Gateway end-to-end timing includes cold loading; decode timing excludes it.
It stops only its own processes and temporary gateway. It never runs in `verify_all`.

Engine logs are retained in `%LOCALAPPDATA%\LocalQwen\engine-logs` across switches;
`native-engine.log.previous` remains for existing tooling. Inspect/remove archived
logs deliberately when no longer needed; no automatic evidence deletion is added.
