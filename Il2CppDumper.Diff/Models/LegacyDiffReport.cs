using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Il2CppDumper.Diff;

internal sealed class LegacyDiffReport
{
    public int schemaVersion => 1;
    public string coverage => "partial";
    public string comparison => "type-name-by-image-and-original-token";
    public string[] unavailable => new[] { "type-identity-across-builds", "member-declarations", "field-offset-drift", "method-addresses", "string-literals" };
    public LegacyDiffEndpoint left { get; init; } = new();
    public LegacyDiffEndpoint right { get; init; } = new();
    public List<LegacyImageComparison> images { get; } = new();
    public int comparedTokens => images.Sum(image => image.sameNames + image.nameDifferences.Count);
    public int sharedOriginalTokens => comparedTokens + images.Sum(image => image.namesUnavailable);
    [JsonIgnore]
    public bool HasDifferences => images.Any(image =>
        image.nameDifferences.Count > 0 || image.leftOnlyTokens.Count > 0 || image.rightOnlyTokens.Count > 0);
}

internal sealed class LegacyDiffEndpoint
{
    public string source { get; init; } = "";
    public string format { get; init; } = "";
    public string manifestStatus { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? declarationsPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? sha256 { get; init; }
    public List<LegacyDllArtifact> assemblies { get; } = new();
    public int typesWithoutImage { get; set; }
}

internal sealed record LegacyDllArtifact(string file, string image, string sha256);

internal sealed class LegacyImageComparison
{
    public string image { get; init; } = "";
    public string status { get; init; } = "";
    public int? leftTypes { get; init; }
    public int? rightTypes { get; init; }
    public int? leftTypesWithoutOriginalToken { get; init; }
    public int? rightTypesWithoutOriginalToken { get; init; }
    public int sameNames { get; set; }
    public int namesUnavailable { get; set; }
    public List<LegacyTokenNameDifference> nameDifferences { get; } = new();
    public List<LegacyTypeIdentity> leftOnlyTokens { get; } = new();
    public List<LegacyTypeIdentity> rightOnlyTokens { get; } = new();
}

internal sealed record LegacyTokenNameDifference(string originalToken, LegacyTypeIdentity left, LegacyTypeIdentity right);

internal sealed record LegacyTypeIdentity(string? name, string originalToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? reconstructedToken = null);
