using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecompOne.Recompiler.Config;

public sealed class RecompOneConfig
{
    [JsonPropertyName("game")] public GameConfig Game { get; set; } = new();
    [JsonPropertyName("cue")] public string Cue { get; set; } = "";
    [JsonPropertyName("elf")] public string? Elf { get; set; }
    [JsonPropertyName("main")] public string? Main { get; set; }
    [JsonPropertyName("functions")] public FunctionEntry[] Functions { get; set; } = [];
    [JsonPropertyName("debug")] public bool Debug { get; set; }
    [JsonPropertyName("overlays")] public OverlayConfig[] Overlays { get; set; } = [];
    [JsonPropertyName("stubs")] public string[] Stubs { get; set; } = [];
    [JsonPropertyName("ignored")] public string[] Ignored { get; set; } = [];
    [JsonPropertyName("patches")] public PatchEntry[] Patches { get; set; } = [];
}

public sealed class PatchEntry
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class GameConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("output")] public string Output { get; set; } = "./Recompiled";
}

public sealed class OverlayConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("elf")] public string? Elf { get; set; }
    [JsonPropertyName("functions")] public FunctionEntry[] Functions { get; set; } = [];
    [JsonPropertyName("stubs")] public string[] Stubs { get; set; } = [];
    [JsonPropertyName("ignored")] public string[] Ignored { get; set; } = [];
}

public sealed class FunctionEntry
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static RecompOneConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RecompOneConfig>(stream, Options)
            ?? throw new InvalidDataException($"failed to parse config {path}");
    }
}
