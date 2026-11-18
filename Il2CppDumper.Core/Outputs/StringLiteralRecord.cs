#nullable enable
using System.Text.Json.Serialization;

namespace Il2CppDumper
{
    public class StringLiteralRecord
    {
        public const string BoundaryStatus = "boundary";

        public uint index { get; set; }
        public string value { get; set; } = string.Empty;
        public int dataIndex { get; set; }
        public int length { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? status { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? raw { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? decryptor { get; set; }
    }
}
