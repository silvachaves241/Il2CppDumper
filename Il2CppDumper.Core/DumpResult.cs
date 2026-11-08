#nullable enable
using System;
using System.Collections.Generic;

namespace Il2CppDumper.Core
{
    public sealed class DumpResult
    {
        public bool Success;
        public string? Error;           // mirrors the #8 failure text
        public IReadOnlyList<string> Warnings;

        /// <summary>
        /// Key-value mapping of artifact identifier to its full path on disk (e.g. "dump.cs" -> "C:\...\dump.cs",
        /// "DummyDll" -> "C:\...\DummyDll"). Contains only artifacts produced or updated in the current execution.
        /// Stale artifacts from previous runs in a reused directory are excluded.
        /// </summary>
        public IReadOnlyDictionary<string, string> Outputs;

        /// <summary>
        /// Exact list of individual file paths written to disk during this specific execution, including
        /// individual assembly DLLs within DummyDll and diagnostic/provenance sidecars.
        /// </summary>
        public IReadOnlyList<string> ProducedFiles;

        public DumpResult()
        {
            Warnings = Array.Empty<string>();
            Outputs = new Dictionary<string, string>();
            ProducedFiles = Array.Empty<string>();
        }

        public DumpResult(
            bool success,
            string? error,
            IReadOnlyList<string>? warnings = null,
            IReadOnlyDictionary<string, string>? outputs = null,
            IReadOnlyList<string>? producedFiles = null)
        {
            Success = success;
            Error = error;
            Warnings = warnings ?? Array.Empty<string>();
            Outputs = outputs ?? new Dictionary<string, string>();
            ProducedFiles = producedFiles ?? Array.Empty<string>();
        }
    }
}
