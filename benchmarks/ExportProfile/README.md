# Large export profile

The runner measures one full `Dumper.Dump` call on .NET 8. Use a fresh process and a fresh output directory for each sample. Build outside the measurement and hash both input files separately before and after the series. The runner prepares the public entry point before starting the clock. First-use JIT within the export and the API's own provenance hashing remain part of the measured work.

The JSON result contains the specified commit, actual runtime, effective configuration, wall time, process peak working set, managed bytes allocated during the API call, and cumulative samples at existing log phase boundaries. Peak working set is the OS high-water mark for the runner process. It is distinct from live managed heap size and total managed allocations.

## Running a before/after series

This example uses the exact output coverage from [#87](https://github.com/silvachaves241/Il2CppDumper/issues/87), including the aggregate dump, both large JSON exports, the native header, dummy assemblies, and provenance and diagnostic outputs. Add `--per-image` on both sides to include the per-image dump introduced after that issue.

```powershell
$benchmark = 'benchmarks/ExportProfile/ExportProfile.csproj'
$baseline = '8585ad24a902c95673b80b19b30d1007354c0375'
$candidate = git rev-parse HEAD
$root = Join-Path $env:TEMP ("il2cpp-profile-" + [guid]::NewGuid().ToString("N"))
$binary = 'C:/path/to/GameAssembly.dll'
$metadata = 'C:/path/to/global-metadata.dat'

New-Item -ItemType Directory $root | Out-Null
git worktree add --detach "$root/source-before" $baseline
dotnet build $benchmark -c Release -o "$root/app-before" "-p:DumperCoreProject=$root/source-before/Il2CppDumper.Core/Il2CppDumper.Core.csproj"
dotnet build $benchmark -c Release -o "$root/app-after"
Get-FileHash -LiteralPath $binary,$metadata

foreach ($sample in 1..3) {
    dotnet "$root/app-before/ExportProfile.dll" $baseline $binary $metadata "$root/before-$sample" > "$root/before-$sample.json"
    if ($LASTEXITCODE -ne 0) { throw "Baseline export failed" }
    dotnet "$root/app-after/ExportProfile.dll" $candidate $binary $metadata "$root/after-$sample" > "$root/after-$sample.json"
    if ($LASTEXITCODE -ne 0) { throw "Candidate export failed" }
}
Get-FileHash -LiteralPath $binary,$metadata
```

Run samples sequentially on the same machine and runtime. Compare the median and range for peak working set, allocation and time. Compare artifact inventories and stable content, alongside warnings, counters, input hashes and config in the provenance files. Normalize only generated timestamps, durations and DLL build identifiers when comparing those files.

Use a separate EventPipe collection to attribute allocation sites. Open the trace in PerfView and inspect the managed allocation stacks. Keep tracing outside the timed series.

```powershell
dotnet-trace collect --profile gc-verbose --show-child-io -o "$root/baseline.nettrace" -- dotnet "$root/app-before/ExportProfile.dll" $baseline $binary $metadata "$root/traced-baseline"
```

CI exercises the large synthetic export cases in `LargeOutputSerializationTests` on both target frameworks. The fixtures check identical JSON bytes and use an allocation budget based on output size. They use generated models and require no game files.
