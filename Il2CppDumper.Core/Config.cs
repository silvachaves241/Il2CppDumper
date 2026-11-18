#nullable enable
using System.Text.Json.Serialization;

namespace Il2CppDumper
{
    public class Config
    {
        /// <summary>Exact image names or "id:N" selectors for declaration exports. Null or empty selects all images.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ImageSelectors { get; set; }

        public bool GenerateAggregateDump { get; set; } = true;
        public bool GeneratePerImageDump { get; set; } = true;
        public bool DumpMethod { get; set; } = true;
        public bool DumpField { get; set; } = true;
        public bool DumpProperty { get; set; } = true;
        public bool DumpAttribute { get; set; } = true;
        public bool DumpFieldOffset { get; set; } = true;
        public bool DumpMethodOffset { get; set; } = true;
        public bool DumpTypeDefIndex { get; set; } = true;
        public bool GenerateDummyDll { get; set; } = true;
        public bool GenerateStruct { get; set; } = true;
        public bool GenerateJsonDump { get; set; } = false;
        public bool ValidateOutputs { get; set; } = false;
        public bool GenerateRunManifest { get; set; } = true;
        public bool GenerateProtectionReport { get; set; } = true;
        public bool GenerateOneofMap { get; set; } = false;
        public bool DummyDllAddToken { get; set; } = true;
        public bool RequireAnyKey { get; set; } = true;
        public bool ForceIl2CppVersion { get; set; } = false;
        public double ForceVersion { get; set; } = 24.3;
        public bool ForceDump { get; set; } = false;
        public bool NoRedirectedPointer { get; set; } = false;
        /// <summary>Analyze PE input without invoking the Windows native loader fallback.</summary>
        public bool StaticOnly { get; set; } = false;
        public string? StringDecryptor { get; set; } = "none";
        public string? StringDecryptorKey { get; set; } = null;
        public bool EmitFridaScript { get; set; } = false;
        public bool EmitBookmarks { get; set; } = false;
        public bool BatchMode { get; set; } = false;
        public bool StopOnFirstError { get; set; } = false;
    }
}
