using System.Net;
using System.Text.Json;
namespace LocalQwenTray;
internal sealed class RecordingCommands : ICommandRunner
{
    public List<string[]> Calls = [];
    public CommandResult Result = new(0,""," ");
    public Task<CommandResult> Run(string file,string[] args,TimeSpan timeout,CancellationToken ct) { Calls.Add([file,..args]); return Task.FromResult(Result); }
}
internal sealed class TestHttp : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests = [];
    public bool CompletionFails, IdentityMismatch;
    public int SavedTokens = 50000;
    public List<string> SlotCalls = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests.Add(request);
        if (request.RequestUri!.AbsolutePath.StartsWith("/slots"))
        {
            SlotCalls.Add(request.RequestUri.PathAndQuery + " " + request.Content!.ReadAsStringAsync(ct).Result);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"n_saved\":"+SavedTokens+",\"n_restored\":"+SavedTokens+"}")});
        }
        bool models = request.RequestUri!.AbsolutePath.EndsWith("/models");
        var content = models ? JsonSerializer.Serialize(new {data=new[]{new {id=IdentityMismatch ? "wrong" : Policy.Model,aliases=IdentityMismatch ? new[]{"wrong"}:new[]{Policy.Model},meta=new{n_ctx=65536}}}})
          : JsonSerializer.Serialize(new{model=Policy.Model,choices=new[]{new{message=new{content="OK"}}}});
        return Task.FromResult(new HttpResponseMessage(!models && CompletionFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK){Content=new StringContent(content)});
    }
}
internal static class BoundaryTests
{
    public static async Task Run()
    {
        var detached=typeof(NativeHost).Assembly.GetType("LocalQwenTray.DetachedNative");
        SelfTests.Check("native child cannot inherit CLI capture pipes",detached is not null);
        var start=detached!.GetMethod("Start");
        var info=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("MODEL_TRAY_TEST_DOTNET") ?? "C:/Program Files/dotnet/dotnet.exe"){UseShellExecute=false};
        info.ArgumentList.Add("--version");
        using(var child=(System.Diagnostics.Process)start!.Invoke(null,new object[]{info})!)
        {using var bounded=new CancellationTokenSource(5000);await child.WaitForExitAsync(bounded.Token);SelfTests.Check("detached native process runs without inherited stdio, exit="+child.ExitCode,child.ExitCode==0);}
        var residency=typeof(NativeHost).GetMethod("ValidateResidency");
        SelfTests.Check("all-GPU native residency gate exists",residency is not null);
        string gpuLog="offloaded 66/66 layers to GPU\nCUDA0 model buffer size = 18620.22 MiB\nCUDA0 KV buffer size = 2304.00 MiB\nCUDA0 KV buffer size = 512.00 MiB\ncreating MTP draft context against the target model\nadding speculative implementation 'draft-mtp'";
        residency!.Invoke(null,new object[]{gpuLog});
        SelfTests.Check("main and shared draft GPU log accepted",true);
        try {residency.Invoke(null,new object[]{gpuLog.Replace("66/66","64/66")});SelfTests.Check("partial GPU offload rejected",false);} catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException is InvalidOperationException){SelfTests.Check("partial GPU offload rejected",true);}
        try {residency.Invoke(null,new object[]{gpuLog+"\nCPU model buffer size = 1.00 MiB"});SelfTests.Check("CPU model buffer rejected",false);} catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException is InvalidOperationException){SelfTests.Check("CPU model buffer rejected",true);}
        residency.Invoke(null,new object[]{gpuLog+"\nllama_context: n_ctx = 131072\nclip_model_loader: CPU model buffer size = 2.00 MiB"});
        SelfTests.Check("vision projector buffers after context creation do not fail the language-model GPU check",true);
        var ownerType=typeof(NativeHost).Assembly.GetType("LocalQwenTray.OwnedNativeProcess");
        SelfTests.Check("native PID start-time exe ownership guard exists",ownerType is not null);
        var matches=ownerType!.GetMethod("Matches");
        using var current=System.Diagnostics.Process.GetCurrentProcess();
        var currentExe=current.MainModule!.FileName;
        var ticks=current.StartTime.ToUniversalTime().Ticks;
        SelfTests.Check("exact process identity matches",(bool)matches!.Invoke(null,new object[]{current.Id,ticks,currentExe})!);
        SelfTests.Check("verified ledger identity remains stable",OwnedNativeProcess.MatchesIdentity(current.Id,ticks));
        SelfTests.Check("PID reuse start time mismatch rejected",!(bool)matches.Invoke(null,new object[]{current.Id,ticks+1,currentExe})!);
        SelfTests.Check("foreign executable rejected",!(bool)matches.Invoke(null,new object[]{current.Id,ticks,"C:/not-owned.exe"})!);
        SelfTests.Check("missing PID rejected",!(bool)matches.Invoke(null,new object[]{int.MaxValue,ticks,currentExe})!);
        SelfTests.Check("128K need covers measured 25,042 MiB engine with vision plus margin; floor is the 64K fallback",Policy.Need(Policy.Ctx128,0) == 25042 + Policy.MarginMiB && Policy.MinimumBudgetMiB == Policy.Need(Policy.Ctx64,0));
        SelfTests.Check("512 MiB fresh snapshot safety headroom",Policy.Budget(new(32000,32607)) == 31488);
        int N(int ctx, int slots = 1) => Policy.Need(ctx, 0, slots);
        SelfTests.Check("128K with one slot loads exactly at its floor", Policy.ChooseLaunch(N(Policy.Ctx128), Policy.Ctx128, 1) == (Policy.Ctx128, 1));
        SelfTests.Check("two slots kept when they fit", Policy.ChooseLaunch(N(Policy.Ctx128, 2), Policy.Ctx128, 2) == (Policy.Ctx128, 2));
        SelfTests.Check("short of VRAM: drop to one slot before shrinking the context", Policy.ChooseLaunch(N(Policy.Ctx128, 2) - 1, Policy.Ctx128, 2) == (Policy.Ctx128, 1));
        SelfTests.Check("still short: fall back to 96K, then 64K", Policy.ChooseLaunch(N(Policy.Ctx128) - 1, Policy.Ctx128, 1) == (Policy.Ctx96, 1) && Policy.ChooseLaunch(N(Policy.Ctx96) - 1, Policy.Ctx128, 1) == (Policy.Ctx64, 1));
        SelfTests.Check("a video editor holding ~4 GB (25.7 GB free) still gets 96K instead of a refusal", Policy.ChooseLaunch(Policy.Budget(new(25716, 32607)), Policy.Ctx256, 2) == (Policy.Ctx96, 1));
        try {Policy.ChooseLaunch(N(Policy.Ctx64)-1,Policy.Ctx256,2);SelfTests.Check("below the 64K floor refuses before loading (never spills to RAM)",false);} catch(InvalidOperationException ex){SelfTests.Check("below the 64K floor refuses before loading (never spills to RAM)",ex.Message.Contains("VRAM") && ex.Message.Contains("Ollama"));}
        SelfTests.Check("256K loads when it fits", Policy.ChooseLaunch(N(Policy.Ctx256, 2), Policy.Ctx256, 2) == (Policy.Ctx256, 2));
        SelfTests.Check("256K falls back to 128K when VRAM is short", Policy.ChooseLaunch(N(Policy.Ctx256) - 1, Policy.Ctx256, 1) == (Policy.Ctx128, 1));
        SelfTests.Check("128K choice never grows to 256K", Policy.ChooseLaunch(31488, Policy.Ctx128, 2).Context == Policy.Ctx128);
        SelfTests.Check("a lighter quant's smaller weights lower the requirement", Policy.ChooseLaunch(N(Policy.Ctx256) - 2000, Policy.Ctx256, 1, 2000) == (Policy.Ctx256, 1));
        SelfTests.Check("smaller contexts need less VRAM, never more", N(Policy.Ctx64) < N(Policy.Ctx96) && N(Policy.Ctx96) < N(Policy.Ctx128) && N(Policy.Ctx128, 2) - N(Policy.Ctx128) == Policy.SlotOverheadMiB);
        SelfTests.Check("context choices are 128K and the trained 256K maximum",Policy.ContextChoices.SequenceEqual(new[]{131072,262144}) && Policy.ContextCap == 262144);
        var fakeRoot=Path.Combine(Path.GetTempPath(),"localqwen-ollama-"+Guid.NewGuid());
        var manifestDir=Path.Combine(fakeRoot,"manifests","registry.ollama.ai","orcarouter","Qwen3.8-27B-Uncensored");
        Directory.CreateDirectory(manifestDir); Directory.CreateDirectory(Path.Combine(fakeRoot,"blobs"));
        File.WriteAllBytes(Path.Combine(fakeRoot,"blobs","sha256-aaa"),new byte[1234]); File.WriteAllBytes(Path.Combine(fakeRoot,"blobs","sha256-bbb"),new byte[10]);
        File.WriteAllText(Path.Combine(manifestDir,"q4_K_M"),"{\"layers\":[{\"mediaType\":\"application/vnd.ollama.image.model\",\"digest\":\"sha256:aaa\"},{\"mediaType\":\"application/vnd.ollama.image.projector\",\"digest\":\"sha256:bbb\"}]}");
        var found=OllamaModels.Find("q4_K_M",fakeRoot);
        SelfTests.Check("Ollama manifest resolves model + vision blobs in place",found is {ModelBytes:1234} && found.Model.EndsWith("sha256-aaa") && found.Projector!.EndsWith("sha256-bbb") && OllamaModels.Find("q6_K",fakeRoot) is null);
        Directory.Delete(fakeRoot,true);
        var cfgDir=Path.Combine(Path.GetTempPath(),"localqwen-cfg-"+Guid.NewGuid()); var appDir=Path.Combine(Path.GetTempPath(),"localqwen-app-"+Guid.NewGuid());
        var fresh=AppConfig.LoadOrCreate(cfgDir,appDir);
        SelfTests.Check("first run creates config.json with a random 32-char key and a local llama-server path",fresh.ApiKey.Length==32 && fresh.LlamaServer==Path.Combine(appDir,"llama.cpp","llama-server.exe") && File.Exists(AppConfig.PathIn(cfgDir)) && fresh.ClientSyncCommand is null);
        SelfTests.Check("config.json is reused on later runs",AppConfig.LoadOrCreate(cfgDir,appDir).ApiKey==fresh.ApiKey && AppConfig.LoadOrCreate(cfgDir,appDir).ApiKey!=AppConfig.LoadOrCreate(Path.Combine(cfgDir,"other"),appDir).ApiKey);
        Directory.Delete(cfgDir,true);
        var argsMethod=typeof(NativeHost).GetMethod("BuildArguments");
        SelfTests.Check("native launch uses verified shared MTP arguments",argsMethod is not null);
        var args=(string[])argsMethod!.Invoke(null,new object[]{"model.gguf","projector.gguf",131072,"log.txt","codex-template.jinja",1})!;
        string argText=string.Join(" ",args);
        SelfTests.Check("one slot MTP5 GPU-only explicit Jinja and fit off",argText.Contains("-np 1") && argText.Contains("--spec-type draft-mtp") && argText.Contains("--spec-draft-n-max 5") && argText.Contains("--fit off") && argText.Contains("-ngl 999") && argText.Contains("--spec-draft-ngl 999") && argText.Contains("token_embd.weight=CUDA0") && args.Contains("--jinja") && !args.Contains("--spec-draft-model") && !args.Contains("--api-key"));
        SelfTests.Check("lossless speculation: MTP + n-gram drafts with probabilistic (rejection-sampled) verification",argText.Contains("--spec-type draft-mtp,ngram-mod") && argText.Contains("--spec-draft-sampling probabilistic") && argText.Contains("--spec-draft-n-max 5"));
        SelfTests.Check("b11429 restores bounded checkpoints and cross-prompt cache",argText.Contains("--ctx-checkpoints 4") && argText.Contains("--cache-ram 8192"));
        // mmap keeps the whole 18.6 GB GGUF resident in system RAM after upload to VRAM (measured 19.0 GB vs 0.8 GB idle).
        SelfTests.Check("weights are not memory-mapped after GPU upload",argText.Contains("--load-mode none"));
        var noVision=string.Join(" ",(string[])argsMethod.Invoke(null,new object?[]{"model.gguf",null,131072,"log.txt","t.jinja",1})!);
        SelfTests.Check("a variant without a projector launches text-only",!noVision.Contains("--mmproj"));
        var embedded=string.Join(" ",(string[])argsMethod.Invoke(null,new object?[]{"model.gguf","p.gguf",131072,"log.txt",null,1})!);
        SelfTests.Check("without a custom template the GGUF's own template is used",embedded.Contains("--jinja") && !embedded.Contains("--chat-template-file"));
        SelfTests.Check("vision projector (mmproj) is loaded",argText.Contains("--mmproj projector.gguf"));
        SelfTests.Check("engine listens only on the private loopback port",argText.Contains($"--host 127.0.0.1 --port {Policy.BackendPort}") && !argText.Contains($"--port {Policy.PublicPort}"));
        SelfTests.Check("Codex-compatible Qwen template is explicit",argText.Contains("--chat-template-file codex-template.jinja"));
        var aliasIndex=Array.IndexOf(args,"--alias");
        // Single-model llama-server serves any requested name (incl. Codex's codex-auto-review), but
        // advertises the alphabetically first alias; a second alias would hijack the advertised ID.
        SelfTests.Check("only the canonical model is advertised",aliasIndex>=0 && args[aliasIndex+1]==Policy.Model);
        var configureEnvironment=typeof(NativeHost).GetMethod("ConfigureEnvironment");
        SelfTests.Check("native launch environment policy exists",configureEnvironment is not null);
        var launchInfo=new System.Diagnostics.ProcessStartInfo();
        launchInfo.Environment["LLAMA_STALE_TEST"]="remove-me";
        launchInfo.Environment["GGML_CUDA_DISABLE_GRAPHS"]="1";
        configureEnvironment!.Invoke(null,new object[]{launchInfo,"test-secret-value"});
        SelfTests.Check("CUDA graphs are not disabled (inherited override cleared)",!launchInfo.Environment.ContainsKey("GGML_CUDA_DISABLE_GRAPHS"));
        SelfTests.Check("stale LLAMA variables are cleared and API key retained",!launchInfo.Environment.ContainsKey("LLAMA_STALE_TEST") && launchInfo.Environment["LLAMA_API_KEY"]=="test-secret-value");
        var commands=new RecordingCommands(); var handler=new TestHttp();
        using var http=new HttpClient(handler);
        var stateDirectory=Path.Combine(Path.GetTempPath(),"localqwen-boundary-"+Guid.NewGuid());
        Directory.CreateDirectory(stateDirectory);
        var testConfig=new AppConfig{ApiKey="test-secret-value",LlamaServer="C:/llama.cpp/llama-server.exe",ClientSyncCommand=["sync-helper.exe","--context","{context}"]};
        var host=new NativeHost(testConfig,commands,http,stateDirectory);
        SelfTests.Check("llama-server path comes from config.json",host.Executable==Path.GetFullPath("C:/llama.cpp/llama-server.exe") && host.ChatTemplatePath is null);
        var leaseMethod=typeof(NativeHost).GetMethod("AcquireLease");
        SelfTests.Check("cross-process lifecycle serialization exists",leaseMethod is not null);
        using(var lease=await (Task<IDisposable>)leaseMethod!.Invoke(host,new object[]{CancellationToken.None})!)
        {
            using var cancel=new CancellationTokenSource(100);
            try {using var second=await (Task<IDisposable>)leaseMethod.Invoke(host,new object[]{cancel.Token})!;SelfTests.Check("simultaneous lifecycle cannot overlap",false);}
            catch(OperationCanceledException){SelfTests.Check("simultaneous lifecycle cannot overlap",true);}
        }
        using(var after=await (Task<IDisposable>)leaseMethod!.Invoke(host,new object[]{CancellationToken.None})!) SelfTests.Check("lifecycle lease releases after cancellation",true);
        File.WriteAllText(host.LogPath,"native-live-log");
        using(var writer=new FileStream(host.LogPath,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
            SelfTests.Check("diagnostics reads active native writer log safely",(await host.Diagnostics(default)).Contains("native-live-log"));
        commands.Result=new(0,"{\"Running\":false,\"ExitCode\":0}","");
        int callsBefore=commands.Calls.Count;
        SelfTests.Check("missing native PID state does not invoke Docker",await host.Inspect(default)==new EngineState(false,false,0) && commands.Calls.Count==callsBefore);
        commands.Result=new(0,"30500,32607\n","");
        SelfTests.Check("single fresh GPU free total sample",await host.FreeVram(default)==new GpuMemory(30500,32607));
        handler.Requests.Clear();
        var probe=await host.Probe(default,false);
        SelfTests.Check("native alias identity and metadata allocated context",probe.Ready && probe.Detail=="context 65536 tokens");
        SelfTests.Check("monitor boundary GET-only",handler.Requests.Count==1 && handler.Requests.All(r=>r.Method==HttpMethod.Get));
        handler.Requests.Clear();
        SelfTests.Check("explicit start requires real completion and authenticated calls",(await host.Probe(default)).Ready && handler.Requests.Count==2 && handler.Requests.All(r=>r.Headers.Authorization?.Parameter=="test-secret-value"));
        handler.CompletionFails=true;
        SelfTests.Check("503 completion not Ready",!(await host.Probe(default)).Ready);
        handler.IdentityMismatch=true;
        SelfTests.Check("mismatched aliases rejected",(await host.Probe(default)).Mismatch);
        await host.SyncContext(new(true,false,"context 65536 tokens"),default);
        SelfTests.Check("optional client-sync hook receives the allocated context",commands.Calls.Last().SequenceEqual(new[]{"sync-helper.exe","--context","65536"}));
        int syncCalls=commands.Calls.Count;
        await host.SyncContext(new(true,false,"context 65536 tokens"),default);
        SelfTests.Check("client-sync hook is skipped when the context did not change",commands.Calls.Count==syncCalls);
        // Session persistence against a live "engine" (this test process stands in for llama-server.exe).
        var sessionDir=Path.Combine(Path.GetTempPath(),"localqwen-session-"+Guid.NewGuid()); Directory.CreateDirectory(sessionDir);
        using var self=System.Diagnostics.Process.GetCurrentProcess();
        var sessionHandler=new TestHttp(); using var sessionHttp=new HttpClient(sessionHandler);
        var sessionHost=new NativeHost(new AppConfig{ApiKey="k",LlamaServer=Environment.ProcessPath!},new RecordingCommands(),sessionHttp,sessionDir);
        File.WriteAllText(Path.Combine(sessionDir,"native-process.json"),JsonSerializer.Serialize(new NativeIdentity(self.Id,self.StartTime.ToUniversalTime().Ticks,Environment.ProcessPath!,131072,25000,"q5_K_M")));
        await sessionHost.SaveSession(default);
        var infoPath=Path.Combine(sessionDir,"sessions","session.json");
        SelfTests.Check("unload saves the open conversation through llama-server's slot API",sessionHandler.SlotCalls.Count==1 && sessionHandler.SlotCalls[0].StartsWith("/slots/0?action=save") && sessionHandler.SlotCalls[0].Contains("session-0.bin") && File.ReadAllText(infoPath).Contains("\"Variant\":\"q5_K_M\"") && File.ReadAllText(infoPath).Contains("131072"));
        await sessionHost.RestoreSession(default);
        SelfTests.Check("a compatible engine restores the saved conversation",sessionHandler.SlotCalls.Count==2 && sessionHandler.SlotCalls[1].StartsWith("/slots/0?action=restore"));
        var compatibleSession = File.ReadAllText(infoPath);
        File.WriteAllText(infoPath,compatibleSession.Replace("\"SpeculativeDecoding\":true", "\"SpeculativeDecoding\":false"));
        await sessionHost.RestoreSession(default);
        SelfTests.Check("a different speculation mode cannot restore the old cache",sessionHandler.SlotCalls.Count==2);
        File.WriteAllText(infoPath,compatibleSession.Replace("\"Fingerprint\":null", "\"Fingerprint\":\"different-engine-arguments\""));
        await sessionHost.RestoreSession(default);
        SelfTests.Check("different native binary or arguments cannot restore the old cache",sessionHandler.SlotCalls.Count==2);
        File.WriteAllText(infoPath,compatibleSession);
        File.WriteAllText(infoPath,File.ReadAllText(infoPath).Replace("\"q5_K_M\"","\"q4_K_M\""));
        await sessionHost.RestoreSession(default);
        SelfTests.Check("a different model variant never gets another's saved state",sessionHandler.SlotCalls.Count==2);
        sessionHandler.SavedTokens=1000; await sessionHost.SaveSession(default);
        SelfTests.Check("short conversations are not kept (re-reading them is faster)",!File.Exists(infoPath));
        File.WriteAllText(Path.Combine(sessionDir,"native-process.json"),JsonSerializer.Serialize(new NativeIdentity(self.Id,self.StartTime.ToUniversalTime().Ticks,Environment.ProcessPath!,131072,25000,"q5_K_M",2)));
        sessionHandler.SavedTokens=50000; sessionHandler.SlotCalls.Clear(); await sessionHost.SaveSession(default);
        SelfTests.Check("with two slots each long conversation is saved", sessionHandler.SlotCalls.Count==2 && sessionHandler.SlotCalls[1].StartsWith("/slots/1?action=save") && File.ReadAllText(infoPath).Contains("\"SavedSlots\":[0,1]"));
        sessionHandler.SlotCalls.Clear(); await sessionHost.RestoreSession(default);
        SelfTests.Check("and both are restored into a matching two-slot engine", sessionHandler.SlotCalls.Count==2 && sessionHandler.SlotCalls.All(c=>c.Contains("action=restore")));
        Directory.Delete(sessionDir,true);
        SelfTests.Check("engine gets a slot save path for sessions",argText.Contains("--slot-save-path"));
        var two=string.Join(" ",(string[])argsMethod.Invoke(null,new object?[]{"m.gguf",null,131072,"log.txt",null,2})!);
        SelfTests.Check("two slots run with one shared KV pool", two.Contains("-np 2 --kv-unified") && argText.Contains("-np 1") && !argText.Contains("--kv-unified"));
        var ocDir=Path.Combine(Path.GetTempPath(),"localqwen-opencode-"+Guid.NewGuid()); Directory.CreateDirectory(ocDir);
        var oc=Path.Combine(ocDir,"opencode.json");
        File.WriteAllText(oc,"{\"$schema\":\"https://opencode.ai/config.json\",\"provider\":{\"ollama\":{\"npm\":\"@ai-sdk/openai-compatible\",\"options\":{\"baseURL\":\"http://127.0.0.1:11434/v1\"}}},\"agent\":{\"mine\":{\"mode\":\"subagent\"}}}");
        SelfTests.Check("OpenCode starts unconnected",!OpenCodeClient.IsConnected(oc));
        SelfTests.Check("connecting writes the local-qwen provider",OpenCodeClient.Connect("k-123",98304,oc) is null && OpenCodeClient.IsConnected(oc));
        var ocText=File.ReadAllText(oc);
        SelfTests.Check("existing OpenCode providers are kept and a backup is made",ocText.Contains("11434") && ocText.Contains("\"mine\"") && ocText.Contains("\"apiKey\": \"k-123\"") && ocText.Contains("98304") && File.Exists(oc+".before-local-qwen"));
        SelfTests.Check("connecting adds the local-qwen agent: edits allowed, shell and web only after approval, nothing outside the folder",
            ocText.Contains("\"external_directory\": \"deny\"") && ocText.Contains("\"bash\": \"ask\"") && ocText.Contains("\"webfetch\": \"ask\"") && ocText.Contains("\"edit\": \"allow\""));
        OpenCodeClient.UpdateContext(131072,oc);
        SelfTests.Check("OpenCode's context limit follows the loaded context",File.ReadAllText(oc).Contains("131072") && !File.ReadAllText(oc).Contains("98304"));
        File.WriteAllText(oc,"{ // my comment\n \"provider\": {} }");
        SelfTests.Check("an OpenCode config with comments is never rewritten",OpenCodeClient.Connect("k",131072,oc) is string && File.ReadAllText(oc).Contains("// my comment"));
        File.Delete(oc); SelfTests.Check("a missing OpenCode config is created",OpenCodeClient.Connect("k",131072,oc) is null && OpenCodeClient.IsConnected(oc));
        Directory.Delete(ocDir,true);
        SelfTests.Check("secret redaction",!Secrets.Redact("Bearer xyz test-secret-value","test-secret-value").Contains("xyz"));
    }
}

