using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace LocalQwenTray;

internal static class ProfileFingerprint
{
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static async Task<string> FileHash(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    public static async Task<object> Read(string name, ModelProfile profile, string executable, string log, CancellationToken ct)
    {
        profile.Validate(name);
        var gguf = await FileHash(profile.ModelFile, ct);
        var template = profile.ChatTemplate is null ? EmbeddedTemplate(profile.ModelFile) : await File.ReadAllTextAsync(profile.ChatTemplate, ct);
        // Hash the runtime DLLs too: the executable's version alone misses CUDA and
        // sampler changes in replacement libraries. No API key is part of this receipt.
        var runtime = new SortedDictionary<string, string>(StringComparer.Ordinal);
        runtime[Path.GetFileName(executable)] = await FileHash(executable, ct);
        foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(executable)!, "*.dll").Order(StringComparer.Ordinal))
            runtime[Path.GetFileName(dll)] = await FileHash(dll, ct);
        var arguments = profile.BuildArguments(name, log);
        var inputFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            // Cover additional file-backed options such as --grammar-file too.
            // Logs are outputs and deliberately excluded from content hashing.
            var candidate = argument.StartsWith('-') && argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : argument;
            if (candidate != log && File.Exists(candidate))
            {
                var fullPath = Path.GetFullPath(candidate);
                inputFiles[fullPath] = fullPath == Path.GetFullPath(profile.ModelFile) ? gguf : await FileHash(candidate, ct);
            }
        }
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var variable = (string)entry.Key;
            if ((variable.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("CUDA_", StringComparison.OrdinalIgnoreCase))
                && !variable.Equals("GGML_CUDA_DISABLE_GRAPHS",StringComparison.OrdinalIgnoreCase)) environment[variable] = entry.Value?.ToString() ?? "";
        }
        var manifest = new
        {
            schema_version = 1, profile = name, gguf_sha256 = gguf,
            projector_sha256 = profile.Projector is null ? null : await FileHash(profile.Projector, ct),
            llama_build = runtime, launch_args = arguments,
            input_files = inputFiles,
            template_sha256 = Hash(Encoding.UTF8.GetBytes(template)),
            environment_sha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(environment)),
            request_policy = "passthrough"
        };
        return new { schema_version = 1, profile = name, sha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(manifest)), manifest };
    }

    // Read metadata only, without loading tensors or invoking llama-server. GGUF v2/v3
    // strings are length-prefixed UTF-8. Unknown types fail closed rather than inventing
    // a template digest that Savannah might accept as a benchmark identity.
    internal static string EmbeddedTemplate(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() is not (2 or 3)) throw new InvalidDataException("Expected GGUF v2/v3");
        reader.ReadUInt64();
        ulong count = reader.ReadUInt64();
        for (ulong i = 0; i < count; i++)
        {
            string key = ReadString(reader);
            uint type = reader.ReadUInt32();
            if (key == "tokenizer.chat_template")
            {
                if (type != 8) throw new InvalidDataException("GGUF chat template is not a string; configure ChatTemplate explicitly");
                return ReadString(reader);
            }
            Skip(reader, type);
        }
        throw new InvalidDataException("GGUF has no tokenizer.chat_template; configure ChatTemplate explicitly");
    }
    static string ReadString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();
        if (length > 64 * 1024 * 1024) throw new InvalidDataException("Oversized GGUF metadata string");
        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }
    static void Skip(BinaryReader reader, uint type)
    {
        if (type == 8) { ReadString(reader); return; }
        if (type == 9)
        {
            uint elementType = reader.ReadUInt32();
            ulong count = reader.ReadUInt64();
            if (count > 10000000 || elementType == 9) throw new InvalidDataException("Invalid GGUF metadata array");
            for (ulong i = 0; i < count; i++) Skip(reader, elementType);
            return;
        }
        int size = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("Unknown GGUF metadata type") };
        if (reader.BaseStream.Position + size > reader.BaseStream.Length) throw new EndOfStreamException();
        reader.BaseStream.Seek(size, SeekOrigin.Current);
    }
}
