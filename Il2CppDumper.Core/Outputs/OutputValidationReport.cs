#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Il2CppDumper.Core
{
    public sealed class OutputValidationReport
    {
        [JsonPropertyName("status")]
        public string Status { get; internal set; } = "notRun";

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; internal set; }

        [JsonPropertyName("artifacts")]
        public List<OutputArtifactValidation> Artifacts { get; } = new();
    }

    public sealed class OutputArtifactValidation
    {
        internal OutputArtifactValidation(string path, string category)
        {
            Path = path;
            Category = category;
        }

        [JsonPropertyName("path")]
        public string Path { get; }

        [JsonPropertyName("category")]
        public string Category { get; }

        [JsonPropertyName("status")]
        public string Status => ErrorCount > 0 ? "failed" : Limitations.Count > 0 ? "partial" : "passed";

        [JsonPropertyName("checks")]
        public Dictionary<string, int> Checks { get; } = new();

        [JsonPropertyName("limitations")]
        public List<string> Limitations { get; } = new();

        [JsonPropertyName("errorCount")]
        public int ErrorCount { get; private set; }

        [JsonPropertyName("findings")]
        public List<OutputValidationFinding> Findings { get; } = new();

        internal void Error(string category, string location, string detail)
        {
            ErrorCount++;
            if (Findings.Count < 50)
            {
                Findings.Add(new OutputValidationFinding(category, location, detail));
            }
        }
    }

    public sealed record OutputValidationFinding(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("location")] string Location,
        [property: JsonPropertyName("detail")] string Detail);
}
