using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Il2CppDumper;
using Il2CppDumper.Core;

if (args.Length != 4 && (args.Length != 5 || args[4] != "--per-image"))
{
    Console.Error.WriteLine("Usage: ExportProfile <commit> <binary> <metadata> <fresh-output-directory> [--per-image]");
    return 2;
}

var output = Path.GetFullPath(args[3]);
if (Directory.Exists(output) || File.Exists(output))
    throw new IOException($"Output path already exists: {output}");

var config = new Config
{
    RequireAnyKey = false,
    GenerateAggregateDump = true,
    GeneratePerImageDump = args.Length == 5,
    GenerateJsonDump = true,
    GenerateStruct = true,
    GenerateDummyDll = true,
    GenerateRunManifest = true,
    GenerateProtectionReport = true,
    GenerateOneofMap = true,
    ValidateOutputs = false,
    EmitFridaScript = false,
    EmitBookmarks = false
};
using var process = Process.GetCurrentProcess();
using var log = new PhaseLog(process);
var request = new DumpRequest(args[1], args[2], output, config, log);
RuntimeHelpers.PrepareMethod(typeof(Dumper).GetMethod(nameof(Dumper.Dump))!.MethodHandle);
log.Start();
var result = Dumper.Dump(request);
var elapsed = log.ElapsedSeconds;
var allocated = log.AllocatedBytes;
process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new
{
    commit = args[0],
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    serverGc = System.Runtime.GCSettings.IsServerGC,
    elapsedSeconds = elapsed,
    peakWorkingSetBytes = process.PeakWorkingSet64,
    allocatedBytes = allocated,
    allocationsByPhase = log.Phases,
    result.Success,
    result.Error,
    result.Warnings,
    files = result.ProducedFiles.Count,
    config
}));
return result.Success ? 0 : 1;

sealed class PhaseLog(Process process) : TextWriter
{
    private readonly Stopwatch clock = new();
    private long initialAllocation;
    private static readonly HashSet<string> Markers =
    [
        "Initializing metadata...", "Initializing il2cpp file...", "Searching...", "Dumping...",
        "Generate struct...", "Generate dummy dll...", "Generate oneof members map...", "Done!"
    ];

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public List<PhaseSample> Phases { get; } = new();
    public double ElapsedSeconds => clock.Elapsed.TotalSeconds;
    public long AllocatedBytes => GC.GetTotalAllocatedBytes(precise: true) - initialAllocation;

    public void Start()
    {
        initialAllocation = GC.GetTotalAllocatedBytes(precise: true);
        clock.Start();
    }

    public override void WriteLine(string? value)
    {
        if (value is null || !Markers.Contains(value))
            return;

        process.Refresh();
        Phases.Add(new PhaseSample(value, ElapsedSeconds, AllocatedBytes, process.PeakWorkingSet64));
    }
}

sealed record PhaseSample(string Marker, double ElapsedSeconds, long AllocatedBytes, long PeakWorkingSetBytes);
