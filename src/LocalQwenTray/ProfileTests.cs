using System.Text;
using System.Text.Json;
namespace LocalQwenTray;

internal static class ProfileTests
{
    public static async Task Run()
    {
        var selected = new List<string?>();
        var admission = new ProfileAdmission((p, _) => { selected.Add(p); return Task.CompletedTask; });
        var first = await admission.Enter(null, default);
        var second = await admission.Enter(null, default);
        using var cancel = new CancellationTokenSource();
        var cancelled = admission.Enter("cancelled", cancel.Token);
        cancel.Cancel();
        try { await cancelled; throw new Exception("Waiting switch did not cancel"); }
        catch (OperationCanceledException) { }
        var extraction = admission.Enter("savannah-extraction", default);
        SelfTests.Check("same-profile requests overlap; a switch waits", selected.Count == 2 && !extraction.IsCompleted);
        first.Dispose();
        SelfTests.Check("switch waits for every active response", !extraction.IsCompleted);
        second.Dispose();
        using (await extraction.WaitAsync(TimeSpan.FromSeconds(3)))
            SelfTests.Check("cancelled switch never selects a profile", selected.SequenceEqual(new string?[] { null, null, "savannah-extraction" }));

        // Reproduce the old idle-save race with an explicit barrier, not a sleep.
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestHost { SaveHook = async _ => { saveStarted.SetResult(); await releaseSave.Task; } };
        var supervisor = new Supervisor(new Controller(host), host: host) { IdleTimeout = TimeSpan.Zero };
        using (await supervisor.AdmitRequest(null, default)) { }
        var unload = supervisor.IdleTick(default);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        SelfTests.Check("slot-save exposes an unloading state", supervisor.IsUnloading && supervisor.State == TrayState.Loading);
        var arriving = supervisor.AdmitRequest(null, default);
        SelfTests.Check("request arriving during idle slot-save waits", !arriving.IsCompleted);
        releaseSave.SetResult();
        await unload;
        using (await arriving.WaitAsync(TimeSpan.FromSeconds(3)))
            SelfTests.Check("request reloads after the idle unload instead of using a dying engine", host.StopCalls == 1 && host.UpCalls == 2 && supervisor.IsReady);

        var dir = Path.Combine(Path.GetTempPath(), "localqwen-profile-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var profile = ModelProfile.Savannah();
            profile.ModelFile = Path.Combine(dir, "model.gguf");
            var exe = Path.Combine(dir, "llama-server.exe");
            await File.WriteAllTextAsync(exe, "synthetic build one");
            void Model(string template)
            {
                using var writer = new BinaryWriter(File.Create(profile.ModelFile));
                writer.Write(0x46554747u); writer.Write(3u); writer.Write(0ul); writer.Write(2ul);
                void S(string value) { var b = Encoding.UTF8.GetBytes(value); writer.Write((ulong)b.Length); writer.Write(b); }
                S("synthetic.array"); writer.Write(9u); writer.Write(4u); writer.Write(2ul); writer.Write(7u); writer.Write(8u);
                S("tokenizer.chat_template"); writer.Write(8u); S(template);
            }
            Model("{{ messages }}");
            async Task<string> Digest(string? log = null)
            {
                var result = await ProfileFingerprint.Read("savannah-extraction", profile, exe, log ?? Path.Combine(dir,"engine.log"), default);
                return JsonSerializer.SerializeToElement(result).GetProperty("sha256").GetString()!;
            }
            var initial = await Digest();
            SelfTests.Check("fingerprint is deterministic and reads the embedded template", initial == await Digest() && ProfileFingerprint.EmbeddedTemplate(profile.ModelFile) == "{{ messages }}");
            Model("changed template");
            var changed = await Digest();
            SelfTests.Check("GGUF/template change invalidates the fingerprint", changed != initial);
            await File.WriteAllTextAsync(exe, "synthetic build two");
            initial = await Digest();
            SelfTests.Check("llama build change invalidates the fingerprint", initial != changed);
            profile.Arguments = [..profile.Arguments, "--seed", "43"];
            changed = await Digest();
            SelfTests.Check("launch flag change invalidates the fingerprint", initial != changed);
            await File.WriteAllTextAsync(Path.Combine(dir,"ggml-cuda.dll"), "synthetic CUDA build");
            SelfTests.Check("runtime DLL change invalidates the fingerprint", changed != await Digest());
            using var http = new HttpClient();
            var config = new AppConfig { ApiKey = "profile-test-key", LlamaServer = exe, Profiles = new() { ["savannah-extraction"] = profile } };
            var native = new NativeHost(config, new RecordingCommands { Result = new(0,"12345, 32607","") }, http, dir);
            var apiHost = new TestHost();
            var apiSupervisor = new Supervisor(new Controller(apiHost),host: apiHost);
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);
            listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await using var gateway = new Gateway(apiSupervisor,config.ApiKey,port,1,dir,config:config,native:native);
            await gateway.StartAsync();
            using var api = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",config.ApiKey);
            using var status = JsonDocument.Parse(await api.GetStringAsync("/v1/status"));
            SelfTests.Check("status returns sampled free VRAM without loading", status.RootElement.GetProperty("free_vram_mib").GetInt32() == 12345 && apiHost.UpCalls == 0);
            using var fingerprint = JsonDocument.Parse(await api.GetStringAsync("/v1/profiles/savannah-extraction/fingerprint"));
            SelfTests.Check("fingerprint endpoint returns the profile digest without loading", fingerprint.RootElement.GetProperty("sha256").GetString() == await Digest(native.LogPath) && apiHost.UpCalls == 0);
            using var missing = await api.GetAsync("/v1/profiles/missing/fingerprint");
            SelfTests.Check("unknown fingerprint returns 404 without loading", missing.StatusCode == System.Net.HttpStatusCode.NotFound && apiHost.UpCalls == 0);
            var args = ModelProfile.Savannah().BuildArguments("savannah-extraction", "engine.log");
            string Value(string flag) => args[Array.IndexOf(args, flag) + 1];
            SelfTests.Check("Savannah matches 32K, one slot, f16 target/draft KV and MTP5", Value("-c") == "32768" && Value("-np") == "1" && Value("-ctk") == "f16" && Value("-ctv") == "f16" && Value("--spec-draft-type-k") == "f16" && Value("--spec-draft-type-v") == "f16" && Value("--spec-type") == "draft-mtp" && Value("--spec-draft-n-max") == "5" && !args.Contains("--ctx-checkpoints") && !args.Contains("--cache-ram") && !args.Contains("--chat-template-file"));
            profile.Arguments = ["--host=0.0.0.0"];
            try { profile.Validate("test"); throw new Exception("Managed argument override accepted"); }
            catch (InvalidOperationException) { SelfTests.Check("profile cannot override loopback binding", true); }
        }
        finally { Directory.Delete(dir, true); }
    }
}
