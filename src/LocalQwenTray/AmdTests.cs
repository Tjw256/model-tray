namespace LocalQwenTray;
internal static class AmdTests
{
    public static void Run()
    {
        var devices = "Available devices:\n  Vulkan0: AMD Radeon RX 9070 XT (16304 MiB, 15416 MiB free)\n  Vulkan1: AMD Radeon(TM) Graphics (16187 MiB, 15378 MiB free)";
        SelfTests.Check("Vulkan memory uses the selected dedicated GPU", NativeHost.ParseVulkanMemory(devices, "Vulkan0", "AMD Radeon RX 9070 XT") == new GpuMemory(15416, 16304));
        SelfTests.Check("Windows physical usage overrides an optimistic Vulkan budget", NativeHost.ConservativeVulkanMemory(new(12422,16304), 12000L * 1048576) == new GpuMemory(4304,16304));
        SelfTests.Check("conservative GPU free memory never goes negative", NativeHost.ConservativeVulkanMemory(new(1000,16304), 20000L * 1048576).FreeMiB == 0);
        NativeHost.ValidatePhysicalResidency("11000000000\n80000000");
        SelfTests.Check("Windows confirms dedicated GPU residency", true);
        foreach (var counters in new[] {"11000000000\n2000000000", "2000000000\n0", "UNKNOWN"})
        {
            try { NativeHost.ValidatePhysicalResidency(counters); SelfTests.Check("shared memory spill or unknown residency is rejected", false); }
            catch (InvalidOperationException) { SelfTests.Check("shared memory spill or unknown residency is rejected", true); }
        }
        try { NativeHost.ParseVulkanMemory(devices, "Vulkan1", "AMD Radeon RX 9070 XT"); SelfTests.Check("integrated GPU cannot substitute for the dedicated GPU", false); }
        catch (InvalidOperationException) { SelfTests.Check("integrated GPU cannot substitute for the dedicated GPU", true); }
        try { NativeHost.ParseVulkanMemory("invalid", "Vulkan0", "AMD Radeon RX 9070 XT"); SelfTests.Check("unavailable Vulkan memory is an error", false); }
        catch (InvalidOperationException) { SelfTests.Check("unavailable Vulkan memory is an error", true); }
        try
        {
            var args = NativeHost.BuildGpuArguments("model.gguf", null, 16384, "engine.log", null, 1, "Vulkan0");
            var text = string.Join(" ", args);
            SelfTests.Check("Vulkan launch explicitly binds main and MTP to RX GPU", text.Contains("--device Vulkan0") && text.Contains("--spec-draft-device Vulkan0") && text.Contains("token_embd.weight=Vulkan0") && !text.Contains("CUDA0") && text.Contains("draft-mtp,ngram-mod"));
            SelfTests.Check("AMD limits retained recurrent states and quantizes draft KV explicitly", text.Contains("--ctx-checkpoints 0") && text.Contains("-b 256 -ub 256") && text.Contains("--spec-draft-type-k q4_0") && text.Contains("--spec-draft-type-v q4_0") && text.Contains("--spec-draft-n-max 1"));
            var plain = NativeHost.BuildGpuArguments("model.gguf", null, 16384, "engine.log", null, 1, "Vulkan0", false);
            SelfTests.Check("plain mode explicitly removes all speculation flags and keeps GPU", !plain.Any(x => x.StartsWith("--spec-")) && plain.Contains("--device") && plain.Contains("token_embd.weight=Vulkan0"));
            var plainNeed = Policy.Need(65536,10000);
            Policy.ConfigureGpu("Vulkan0", false, true);
            SelfTests.Check("plain memory budget excludes the absent MTP buffers", plainNeed < Policy.Need(65536,10000));
            SelfTests.Check("AMD profile offers bounded contexts and installed IQ2 quant", Policy.ContextChoices.SequenceEqual(new[] {8192,16384,32768,65536}) && Policy.MinimumContext == 8192 && new AppSettings { Variant = "iq2_xxs", ContextTokens = 16384, ParallelRequests = 1 }.Choice == new LaunchChoice("iq2_xxs", 16384, 1));
            var budget = Policy.Need(8192, 10000);
            SelfTests.Check("AMD memory fallback shrinks context without changing quant or MTP", Policy.ChooseLaunch(budget, 16384, 1, 10000) == (8192, 1));
            try { Policy.ChooseLaunch(budget - 1, 16384, 1, 10000); SelfTests.Check("AMD refuses insufficient memory", false); }
            catch (InvalidOperationException) { SelfTests.Check("AMD refuses insufficient memory", true); }
            var log = "offloaded 66/66 layers to GPU\nVulkan0 model buffer size = 8462.44 MiB\nVulkan0 KV buffer size = 300 MiB\nVulkan0 KV buffer size = 50 MiB\ncreating MTP draft context against the target model\nadding speculative implementation 'draft-mtp'";
            NativeHost.ValidateVulkanResidency(log, "Vulkan0");
            SelfTests.Check("Vulkan validates main and MTP GPU buffers", true);
            NativeHost.ValidateVulkanResidency("offloaded 66/66 layers to GPU\nVulkan0 model buffer size = 8462.44 MiB\nVulkan0 KV buffer size = 300 MiB", "Vulkan0", false);
            SelfTests.Check("explicit plain mode validates GPU without requiring MTP", true);
            foreach (var invalid in new[] {log.Replace("66/66", "60/66"), log.Replace("Vulkan0", "Vulkan1"), log.Replace("adding speculative implementation 'draft-mtp'", "none"), log + "\nCPU KV buffer size = 100 MiB", log + "\nVulkan_Host model buffer size = 200 MiB"})
            {
                try { NativeHost.ValidateVulkanResidency(invalid, "Vulkan0"); SelfTests.Check("Vulkan rejects offload, wrong GPU or missing MTP", false); }
                catch (InvalidOperationException) { SelfTests.Check("Vulkan rejects offload, wrong GPU or missing MTP", true); }
            }
        }
        finally { Policy.ConfigureGpu("CUDA0"); }
    }
}
