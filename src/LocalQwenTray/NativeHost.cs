using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace LocalQwenTray;
internal record CommandResult(int ExitCode, string Output, string Error);
internal interface ICommandRunner { Task<CommandResult> Run(string file, string[] args, TimeSpan timeout, CancellationToken ct); }
internal record NativeIdentity(int Pid, long StartTimeUtcTicks, string Executable, int ContextTokens, int BudgetMiB, string? Variant = null);
internal sealed class NativeHost(AppConfig config, ICommandRunner commands, HttpClient http, string? stateDirectory = null) : IQwenHost
{
    readonly string key = config.ApiKey;
    public string Executable => Path.GetFullPath(config.LlamaServer);
    public LaunchChoice Choice { get; set; } = LaunchChoice.Default;
    public (string Variant, int Context)? Loaded() => ReadIdentity() is { } id && Valid(id) ? (id.Variant ?? Policy.DefaultVariant, id.ContextTokens) : null;
    string StateDirectory => stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LocalQwen");
    public string StatePath => Path.Combine(StateDirectory,"native-process.json");
    public string LogPath => Path.Combine(StateDirectory,"native-engine.log");
    // Optional custom chat template; without one the template embedded in the GGUF is used.
    public string? ChatTemplatePath => string.IsNullOrWhiteSpace(config.ChatTemplate) ? null : Path.GetFullPath(config.ChatTemplate);
    // Speculative decoding (all lossless; the model verifies every drafted token):
    //  - draft-mtp: Qwen3.8's built-in multi-token-prediction head drafts 5 tokens (5 measured fastest).
    //  - ngram-mod: drafts long runs copied from the context (file edits, echoed tool output): 174 -> 365-385 tok/s.
    //  - probabilistic drafts + rejection sampling match sampled (temperature > 0) output: +6-14% on thinking/answers.
    //  Measured 2026-10-09 at 48K context on an RTX 5090 (b11517).
    public static string[] BuildArguments(string model, string? projector, int context, string log, string? chatTemplate) => ["-m",model,..(projector is null ? Array.Empty<string>() : ["--mmproj",projector]),"-ngl","999","--fit","off","--load-mode","none","-c",context.ToString(),"-np","1","-t","4","-tb","4","-b","512","-ub","512","-fa","on","-ctk","q4_0","-ctv","q4_0","--spec-type","draft-mtp,ngram-mod","--spec-draft-sampling","probabilistic","--no-host","--spec-draft-n-max","5","--spec-draft-ngl","999","--spec-draft-threads","4","--spec-draft-threads-batch","4","--ctx-checkpoints","4","--cache-ram","8192","-lv","4","--override-tensor","token_embd.weight=CUDA0","--host","127.0.0.1","--port",Policy.BackendPort.ToString(),"--alias",Policy.Model,"--jinja",..(chatTemplate is null ? Array.Empty<string>() : ["--chat-template-file",chatTemplate]),"--log-colors","off","--log-file",log];
    public static void ConfigureEnvironment(ProcessStartInfo info,string apiKey)
    {
        foreach(var name in info.Environment.Keys.Where(x=>x.StartsWith("LLAMA_",StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["LLAMA_API_KEY"]=apiKey;
        // CUDA graphs stay ON: disabling them cost ~28% decode speed (174 -> 125 tok/s, isolated A/B
        // 2026-10-06) and did not stop the sustained-load GPU faults, which also occurred with graphs on.
        info.Environment.Remove("GGML_CUDA_DISABLE_GRAPHS");
    }
    static async Task<string> ReadLiveLog(string path,CancellationToken ct)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
    public static void ValidateResidency(string log)
    {
        // The vision projector loads after the language model's context is created and logs its own buffers;
        // the CPU-weights check applies to the language-model load section only.
        int contextStart=log.IndexOf("llama_context:",StringComparison.Ordinal);
        string modelSection=contextStart>=0 ? log[..contextStart] : log;
        if(!Regex.IsMatch(log,@"offloaded (\d+)/\1 layers to GPU") || !log.Contains("CUDA0 model buffer size") || Regex.IsMatch(modelSection,@"(?m)(?:CPU\w*|CUDA_Host)\s+model buffer size") || Regex.Matches(log,@"CUDA0 KV buffer size").Count < 2 || Regex.IsMatch(log,@"(?:CPU\w*|CUDA_Host)\s+KV buffer size") || !log.Contains("creating MTP draft context against the target model") || !log.Contains("adding speculative implementation 'draft-mtp'")) throw new InvalidOperationException("Native GPU-only residency verification failed; CPU model weights or missing main/draft GPU KV evidence");
    }
    public async Task VerifyResidency(ProbeResult probe,CancellationToken ct)
    {
        var identity=ReadIdentity();
        if(identity is null || !Valid(identity)) throw new InvalidOperationException("Native ownership lost before residency verification");
        if(probe.Detail!=$"context {identity.ContextTokens} tokens") throw new InvalidOperationException("Allocated context differs from requested native budget");
        ValidateResidency(await ReadLiveLog(LogPath,ct));
    }
    public async Task<IDisposable> AcquireLease(CancellationToken ct)
    {
        Directory.CreateDirectory(StateDirectory);
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            try {return new FileStream(Path.Combine(StateDirectory,"native-lifecycle.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException) {await Task.Delay(100,ct);}
        }
    }
    void Ensure(CommandResult result)
    {
        if(result.ExitCode!=0) throw new InvalidOperationException($"Command failed (exit {result.ExitCode}): "+Secrets.Redact(result.Error+" "+result.Output,key));
    }
    NativeIdentity? ReadIdentity()
    {
        if(!File.Exists(StatePath)) return null;
        try {return JsonSerializer.Deserialize<NativeIdentity>(File.ReadAllText(StatePath));}
        catch(JsonException) {return null;}
    }
    // Polling establishes liveness only. Launch and termination still require the complete
    // PID + creation-time + executable identity, so a reused PID can never be terminated.
    bool Valid(NativeIdentity identity) => string.Equals(Path.GetFullPath(identity.Executable),Executable,StringComparison.OrdinalIgnoreCase) && OwnedNativeProcess.Exists(identity.Pid);
    public Task<EngineState> Inspect(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var identity=ReadIdentity();
        return Task.FromResult(identity is null ? new EngineState(false,false,0) : new EngineState(true,Valid(identity),0));
    }
    public async Task<GpuMemory> FreeVram(CancellationToken ct)
    {
        var result=await commands.Run("nvidia-smi",["--query-gpu=memory.free,memory.total","--format=csv,noheader,nounits","--id=0"],TimeSpan.FromSeconds(15),ct);
        Ensure(result); var fields=result.Output.Trim().Split(',');
        if(fields.Length!=2 || !int.TryParse(fields[0].Trim(),out int free) || !int.TryParse(fields[1].Trim(),out int total)) throw new InvalidOperationException("Cannot parse NVIDIA GPU 0 free/total memory");
        return new(free,total);
    }
    async Task<bool> Listening(CancellationToken ct)
    {
        using var client=new TcpClient();
        try {await client.ConnectAsync("127.0.0.1",Policy.BackendPort,ct);return true;}
        catch(SocketException){return false;}
    }
    public async Task LaunchNative(GpuMemory memory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if((await Inspect(ct)).Running) throw new InvalidOperationException("Owned process already exists; use Start to adopt it");
        if(await Listening(ct)) throw new InvalidOperationException($"Unowned listener on internal port {Policy.BackendPort}; no process was stopped");
        var choice=Choice;
        var files=OllamaModels.Find(choice.Variant)
            ?? throw new FileNotFoundException($"Model {choice.Variant} is not downloaded in Ollama; run: ollama pull {OllamaModels.Repository}:{choice.Variant}");
        // VRAM needs are measured on the reference quant; others differ by roughly their weights-file size.
        int saving=Policy.ReferenceWeightsMiB-(int)(files.ModelBytes/(1024*1024));
        int budget=Policy.Budget(memory),context=Policy.ChooseContext(budget,choice.Context,saving);
        if(!File.Exists(Executable)) throw new FileNotFoundException($"llama-server.exe not found at {Executable}; set LlamaServer in {AppConfig.PathIn(StateDirectory)}");
        if(ChatTemplatePath is not null && !File.Exists(ChatTemplatePath)) throw new FileNotFoundException($"Chat template not found at {ChatTemplatePath}; fix or clear ChatTemplate in config.json");
        Directory.CreateDirectory(StateDirectory);
        // Separate per-load engine log; credentials travel only through the child environment.
        if(File.Exists(LogPath)) File.Copy(LogPath,LogPath+".previous",true);
        File.WriteAllText(LogPath,"");
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(Executable)!};
        ConfigureEnvironment(info,key);
        foreach(var arg in BuildArguments(files.Model,files.Projector,context,LogPath,ChatTemplatePath)) info.ArgumentList.Add(arg);
        using var process=DetachedNative.Start(info);
        try
        {
            var identity=new NativeIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks,Executable,context,budget,choice.Variant);
            for(int i=0; i<50 && !OwnedNativeProcess.Matches(identity.Pid,identity.StartTimeUtcTicks,Executable); i++)
            {
                if(process.HasExited) throw new InvalidOperationException("Native process exited before ownership could be verified");
                await Task.Delay(100,ct);
            }
            if(!OwnedNativeProcess.Matches(identity.Pid,identity.StartTimeUtcTicks,Executable)) throw new InvalidOperationException("Native process ownership could not be verified after launch");
            var staged=StatePath+".tmp";
            File.WriteAllText(staged,JsonSerializer.Serialize(identity)); File.Move(staged,StatePath,true);
        }
        catch {if(!process.HasExited)process.Kill();throw;}
    }
    public async Task Stop(CancellationToken ct)
    {
        var identity=ReadIdentity();
        if(identity is not null && Valid(identity))
        {
            using var process=Process.GetProcessById(identity.Pid);
            // Recheck this handle, not just the PID, immediately before termination.
            if(!OwnedNativeProcess.MatchesHandle(process,identity.StartTimeUtcTicks,Executable)) throw new InvalidOperationException("Process ownership changed; refusing Stop");
            if(!process.HasExited) process.Kill();
            await process.WaitForExitAsync(ct);
        }
        if((await Inspect(ct)).Running) throw new InvalidOperationException("Owned process did not exit");
        for(int i=0; i<50 && await Listening(ct); i++) await Task.Delay(100,ct);
        if(await Listening(ct)) throw new InvalidOperationException($"Internal port {Policy.BackendPort} still open; unknown listener was not stopped");
        // Retain identity as a stopped receipt; never delete another process's ledger.
    }
    public async Task<ProbeResult> Probe(CancellationToken ct, bool verifyInference = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = Request(HttpMethod.Get, "models");
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode) return new(false, false, $"models HTTP {(int)response.StatusCode}");
            using var models = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            var entries = models.RootElement.GetProperty("data").EnumerateArray().ToArray();
            bool Matches(JsonElement entry) => entry.GetProperty("id").GetString() == Policy.Model || entry.TryGetProperty("aliases", out var aliases) && aliases.EnumerateArray().Any(x => x.GetString() == Policy.Model);
            if (!entries.Any(Matches)) return new(false,true);
            var entry = entries.First(Matches);
            var responseIdentity = entry.GetProperty("id").GetString();
            int context = entry.GetProperty("meta").GetProperty("n_ctx").GetInt32();
            if (context < Policy.MinimumContext || context > Policy.ContextCap) return new(false,true,"Invalid allocated agent context");
            string detail = $"context {context} tokens";
            // A live models response observes engine availability without occupying a sequence.
            if (!verifyInference) return new(true, false, detail);
            using var completion = Request(HttpMethod.Post, "chat/completions");
            completion.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = Policy.Model, messages = new[] { new { role = "user", content = "Reply OK." } },
                max_tokens = 16, temperature = 0, stream = false,
                chat_template_kwargs = new { enable_thinking = false }
            }), Encoding.UTF8, "application/json");
            using var answer = await http.SendAsync(completion, deadline.Token);
            if (!answer.IsSuccessStatusCode) return new(false, false, $"completion HTTP {(int)answer.StatusCode}");
            using var body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(deadline.Token));
            var root = body.RootElement;
            if (!root.TryGetProperty("model", out var model) || model.GetString() != responseIdentity) return new(false, true);
            bool ready = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString());
            return new(ready, false, detail);
        }
        catch (HttpRequestException ex) { return new(false, false, Secrets.Redact(ex.Message, key)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, false, "Readiness request timed out"); }
        catch (JsonException) { return new(false, false, "Invalid readiness response JSON"); }
        catch (KeyNotFoundException) { return new(false, false, "Missing readiness response fields"); }
    }
    HttpRequestMessage Request(HttpMethod method, string suffix)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{Policy.BackendPort}/v1/" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
    public async Task SyncContext(ProbeResult probe, CancellationToken ct)
    {
        var match = Regex.Match(probe.Detail, @"^context (\d+) tokens$");
        if (!probe.Ready || !match.Success) throw new InvalidOperationException("Model loaded but actual context unavailable; client metadata was not updated");
        // Optional hook (config ClientSyncCommand) that tells clients the allocated context; runs only when it changed.
        if (config.ClientSyncCommand is not { Length: > 0 } command) return;
        var marker = Path.Combine(StateDirectory, "client-context-synced.txt");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == match.Groups[1].Value) return;
        Ensure(await commands.Run(command[0], command.Skip(1).Select(a => a.Replace("{context}", match.Groups[1].Value)).ToArray(), TimeSpan.FromSeconds(90), ct));
        File.WriteAllText(marker, match.Groups[1].Value);
    }
    // Short poll: with on-demand loading a client request is waiting on readiness.
    public Task Delay(CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(1), ct);
    public async Task<string> Diagnostics(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = File.Exists(LogPath) ? await ReadLiveLog(LogPath,ct) : "No native engine log yet";
        return Secrets.Redact("Native diagnostic tail:\n" + string.Join('\n',text.Split('\n').TakeLast(100)),key);
    }
}
internal static class Secrets
{
    public static string Redact(string text, string key)
    {
        if (!string.IsNullOrEmpty(key)) text = text.Replace(key, "[REDACTED]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?i)Bearer\s+[^\s'"",}\]]+", "Bearer [REDACTED]");
        return Regex.Replace(text, @"(?i)(api[_-]?key\s*['""\s:=]+)(\[[^\]]*\]|[^\s,}\]]+)", "$1[REDACTED]");
    }
}
