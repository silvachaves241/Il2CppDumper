using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Il2CppDumper.Diff;

public sealed class RenameCandidates
{
    public List<TypeRenameCandidate> types { get; init; } = new();
    public List<FieldRenameCandidate> fields { get; init; } = new();
}

public sealed record TypeRenameEndpoint(string type, string? assembly, int? typeDefIndex, uint? typeToken);

public sealed record TypeRenameCandidate(TypeRenameEndpoint[] left, TypeRenameEndpoint[] right, string[] evidence, string[] ambiguities)
{
    public string status => ambiguities.Length == 0 ? "candidate" : "ambiguous";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RenameDeclarationChange>? declarationChanges { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldOffsetStructDrift? layoutChanges { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? matchingInstanceOffsets { get; init; }
}

public sealed record RenameDeclarationChange(JsonElement left, JsonElement right);

public sealed record FieldRenameCandidate(string type, string offset, JsonDumpField[] left, JsonDumpField[] right, string[] ambiguities)
{
    public string status => ambiguities.Length == 0 ? "candidate" : "ambiguous";
}
