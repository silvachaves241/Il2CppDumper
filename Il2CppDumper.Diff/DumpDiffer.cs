#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Il2CppDumper.Diff
{
    public static class DumpDiffer
    {
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static int Execute(string leftPath, string rightPath, string outputPath, TextWriter? log = null)
        {
            try
            {
                var result = Compare(leftPath, rightPath);

                string targetFile;
                if (outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    targetFile = Path.GetFullPath(outputPath);
                }
                else
                {
                    targetFile = Path.Combine(Path.GetFullPath(outputPath), "dump-diff.json");
                }

                var parentDir = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                var json = JsonSerializer.Serialize(result, JsonOptions);
                File.WriteAllText(targetFile, json, new UTF8Encoding(false));

                if (result.HasDifferences)
                {
                    log?.WriteLine($"Differences found: {result.summary.addedTypes} added types, {result.summary.removedTypes} removed types, {result.types.changed.Count} changed types, {result.summary.addressChanges} address changes, {result.summary.valueChanges} literal value changes.");
                    log?.WriteLine($"Diff written to: {targetFile}");
                    return 1;
                }
                else
                {
                    log?.WriteLine("No differences found.");
                    log?.WriteLine($"Diff written to: {targetFile}");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                log?.WriteLine($"ERROR: {ex.Message}");
                return 2;
            }
        }

        public static DumpDiffResult Compare(string leftPath, string rightPath)
        {
            var leftArtifacts = ResolveArtifacts(leftPath, "left");
            var rightArtifacts = ResolveArtifacts(rightPath, "right");
            return Compare(leftArtifacts, rightArtifacts);
        }

        public static DumpDiffResult Compare(JsonDumpModel leftModel, JsonDumpModel rightModel)
        {
            var leftArtifacts = new ParsedArtifacts { Model = leftModel };
            var rightArtifacts = new ParsedArtifacts { Model = rightModel };
            return Compare(leftArtifacts, rightArtifacts);
        }

        private static DumpDiffResult Compare(ParsedArtifacts leftArtifacts, ParsedArtifacts rightArtifacts)
        {
            var result = new DumpDiffResult
            {
                left = leftArtifacts.Endpoint,
                right = rightArtifacts.Endpoint
            };

            // 1. Compare Types
            var reconciled = TypeReconciler.Reconcile(leftArtifacts.Model, rightArtifacts.Model);

            result.types.added = reconciled.Where(r => r.IsAdded).Select(r => r.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            result.types.removed = reconciled.Where(r => r.IsRemoved).Select(r => r.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

            foreach (var item in reconciled.Where(r => r.IsMatched).OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                var typeName = item.Key;
                var leftType = item.Left!;
                var rightType = item.Right!;

                // Compare fields (offset is passthrough only; only type change triggers changedField)
                var leftFields = (leftType.fields ?? new List<JsonDumpField>())
                    .GroupBy(f => f.name)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var rightFields = (rightType.fields ?? new List<JsonDumpField>())
                    .GroupBy(f => f.name)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                var addedFields = rightFields.Keys.Except(leftFields.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var removedFields = leftFields.Keys.Except(rightFields.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
                var commonFields = leftFields.Keys.Intersect(rightFields.Keys).OrderBy(x => x, StringComparer.Ordinal);

                var changedFields = new List<DumpDiffFieldChange>();
                foreach (var fName in commonFields)
                {
                    var lf = leftFields[fName];
                    var rf = rightFields[fName];
                    if (lf.type != rf.type)
                    {
                        changedFields.Add(new DumpDiffFieldChange
                        {
                            field = fName,
                            left = new DumpDiffFieldDetails { type = lf.type, offset = lf.offset },
                            right = new DumpDiffFieldDetails { type = rf.type, offset = rf.offset }
                        });
                    }
                }

                // Compare methods
                var leftMethods = (leftType.methods ?? new List<JsonDumpMethod>())
                    .GroupBy(m => m.name)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
                var rightMethods = (rightType.methods ?? new List<JsonDumpMethod>())
                    .GroupBy(m => m.name)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

                var addedMethods = rightMethods.Keys.Except(leftMethods.Keys).ToList();
                var removedMethods = leftMethods.Keys.Except(rightMethods.Keys).ToList();
                var commonMethods = leftMethods.Keys.Intersect(rightMethods.Keys).OrderBy(x => x, StringComparer.Ordinal);

                var changedMethods = new List<DumpDiffMethodChange>();
                foreach (var mName in commonMethods)
                {
                    var lList = leftMethods[mName];
                    var rList = rightMethods[mName];
                    if (lList.Count == 1 && rList.Count == 1)
                    {
                        var lm = lList[0];
                        var rm = rList[0];
                        var lp = FormatParams(lm);
                        var rp = FormatParams(rm);
                        if (lm.returnType != rm.returnType || lp != rp)
                        {
                            changedMethods.Add(new DumpDiffMethodChange
                            {
                                method = mName,
                                left = new DumpDiffMethodDetails { returnType = lm.returnType, parameters = lp },
                                right = new DumpDiffMethodDetails { returnType = rm.returnType, parameters = rp }
                            });
                        }
                    }
                    else
                    {
                        // Overloads: compare by formatted signature counts, never throw on duplicate signatures
                        var lSigList = lList.Select(FormatSignature).ToList();
                        var rSigList = rList.Select(FormatSignature).ToList();

                        var lSigCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var s in lSigList)
                            lSigCounts[s] = lSigCounts.GetValueOrDefault(s, 0) + 1;

                        var rSigCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var s in rSigList)
                            rSigCounts[s] = rSigCounts.GetValueOrDefault(s, 0) + 1;

                        foreach (var (sig, rCount) in rSigCounts)
                        {
                            var lCount = lSigCounts.GetValueOrDefault(sig, 0);
                            for (var c = 0; c < rCount - lCount; c++)
                            {
                                addedMethods.Add(sig);
                            }
                        }

                        foreach (var (sig, lCount) in lSigCounts)
                        {
                            var rCount = rSigCounts.GetValueOrDefault(sig, 0);
                            for (var c = 0; c < lCount - rCount; c++)
                            {
                                removedMethods.Add(sig);
                            }
                        }
                    }
                }

                addedMethods.Sort(StringComparer.Ordinal);
                removedMethods.Sort(StringComparer.Ordinal);
                changedMethods = changedMethods.OrderBy(x => x.method, StringComparer.Ordinal).ToList();

                if (addedFields.Count > 0 || removedFields.Count > 0 || changedFields.Count > 0 ||
                    addedMethods.Count > 0 || removedMethods.Count > 0 || changedMethods.Count > 0)
                {
                    result.types.changed.Add(new DumpDiffTypeChange
                    {
                        type = item.Key,
                        image = item.Image,
                        assembly = item.Assembly,
                        fields = new DumpDiffFieldChanges
                        {
                            added = addedFields,
                            removed = removedFields,
                            changed = changedFields
                        },
                        methods = new DumpDiffMethodChanges
                        {
                            added = addedMethods,
                            removed = removedMethods,
                            changed = changedMethods
                        }
                    });
                }
            }

            // 2. Compare Method Addresses
            if (leftArtifacts.ScriptMethods != null && rightArtifacts.ScriptMethods != null)
            {
                var addressChanges = new List<DumpDiffAddressChange>();
                var ambiguityNotes = new List<string>();

                var leftIndex = TypeLookupIndex.Build(reconciled, isLeft: true);
                var rightIndex = TypeLookupIndex.Build(reconciled, isLeft: false);

                var leftItems = leftArtifacts.ScriptMethods.Select(m => new MethodDiffItem
                {
                    Entry = m,
                    Identity = leftIndex.Resolve(m, isLeft: true)
                }).ToList();

                var rightItems = rightArtifacts.ScriptMethods.Select(m => new MethodDiffItem
                {
                    Entry = m,
                    Identity = rightIndex.Resolve(m, isLeft: false)
                }).ToList();

                // Phase 1: Correlate qualified pairs (demonstrably unique by qualified key)
                var leftQualified = leftItems
                    .Where(x => x.Identity.QualifiedKey != null)
                    .GroupBy(x => (QualifiedKey: x.Identity.QualifiedKey!, MethodName: x.Entry.MethodName))
                    .ToDictionary(g => g.Key, g => g.ToList());

                var rightQualified = rightItems
                    .Where(x => x.Identity.QualifiedKey != null)
                    .GroupBy(x => (QualifiedKey: x.Identity.QualifiedKey!, MethodName: x.Entry.MethodName))
                    .ToDictionary(g => g.Key, g => g.ToList());

                var commonQualifiedKeys = leftQualified.Keys.Intersect(rightQualified.Keys)
                    .OrderBy(k => k.QualifiedKey, StringComparer.Ordinal)
                    .ThenBy(k => k.MethodName, StringComparer.Ordinal);

                foreach (var qKey in commonQualifiedKeys)
                {
                    var leftEntries = leftQualified[qKey];
                    var rightEntries = rightQualified[qKey];

                    // Step 1: Correlate by explicit managed identity (MethodSignature) first
                    var leftByMethodSig = leftEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.MethodSignature))
                        .GroupBy(m => m.Entry.MethodSignature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var rightByMethodSig = rightEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.MethodSignature))
                        .GroupBy(m => m.Entry.MethodSignature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var commonMethodSigs = leftByMethodSig.Keys.Intersect(rightByMethodSig.Keys)
                        .OrderBy(s => s, StringComparer.Ordinal);

                    foreach (var msig in commonMethodSigs)
                    {
                        var leftMs = leftByMethodSig[msig];
                        var rightMs = rightByMethodSig[msig];

                        foreach (var e in leftMs) e.Handled = true;
                        foreach (var e in rightMs) e.Handled = true;

                        if (leftMs.Count == 1 && rightMs.Count == 1)
                        {
                            var lm = leftMs[0];
                            var rm = rightMs[0];
                            if (lm.Entry.Address != rm.Entry.Address)
                            {
                                var displayMethod = !string.IsNullOrEmpty(lm.Entry.Signature)
                                    ? lm.Entry.Signature
                                    : (!string.IsNullOrEmpty(rm.Entry.Signature) ? rm.Entry.Signature : msig);
                                var displayImage = rm.Identity.Image ?? lm.Identity.Image ?? rm.Entry.Image ?? lm.Entry.Image;
                                var displayAssembly = rm.Identity.Assembly ?? lm.Identity.Assembly ?? rm.Entry.Assembly ?? lm.Entry.Assembly;
                                var displayMethodSig = rm.Entry.MethodSignature ?? lm.Entry.MethodSignature;
                                addressChanges.Add(new DumpDiffAddressChange
                                {
                                    type = lm.Entry.TypeName,
                                    image = displayImage,
                                    assembly = displayAssembly,
                                    method = displayMethod,
                                    methodSignature = displayMethodSig,
                                    left = $"0x{lm.Entry.Address:X}",
                                    right = $"0x{rm.Entry.Address:X}"
                                });
                            }
                        }
                        else
                        {
                            var sortedLeft = leftMs.Select(e => e.Entry.Address).OrderBy(a => a).ToList();
                            var sortedRight = rightMs.Select(e => e.Entry.Address).OrderBy(a => a).ToList();

                            if (sortedLeft.Count == sortedRight.Count &&
                                sortedLeft.Distinct().Count() <= 1 &&
                                sortedLeft.SequenceEqual(sortedRight))
                            {
                                // Shared native implementation across all entries: unchanged
                            }
                            else
                            {
                                result.hasAmbiguousMethodAddresses = true;
                                ambiguityNotes.Add(msig);
                            }
                        }
                    }

                    // Step 2: Correlate remaining unhandled entries in this qKey (where at least one side lacks MethodSignature)
                    var remLeft = leftEntries.Where(e => !e.Handled).ToList();
                    var remRight = rightEntries.Where(e => !e.Handled).ToList();

                    if (remLeft.Count == 0 || remRight.Count == 0)
                        continue;

                    // If all remaining entries on both sides have explicit MethodSignature, they are incompatible managed overloads
                    if (remLeft.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)) &&
                        remRight.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)))
                    {
                        continue;
                    }

                    if (remLeft.Count == 1 && remRight.Count == 1)
                    {
                        var lm = remLeft[0];
                        var rm = remRight[0];

                        if (!string.IsNullOrEmpty(lm.Entry.MethodSignature) && !string.IsNullOrEmpty(rm.Entry.MethodSignature))
                        {
                            continue;
                        }

                        var leftHasSig = !string.IsNullOrEmpty(lm.Entry.Signature);
                        var rightHasSig = !string.IsNullOrEmpty(rm.Entry.Signature);

                        if (leftHasSig && rightHasSig && !AreSignaturesCompatible(lm, rm, qKey.MethodName))
                        {
                            // Distinct overloads with different signatures: do not pair.
                            continue;
                        }

                        if (IsSignatureColliding(lm, qKey.MethodName) || IsSignatureColliding(rm, qKey.MethodName))
                        {
                            lm.Handled = true;
                            rm.Handled = true;
                            result.hasAmbiguousMethodAddresses = true;
                            var desc = leftHasSig ? lm.Entry.Signature! : (rightHasSig ? rm.Entry.Signature! : $"{lm.Entry.TypeName}.{qKey.MethodName}");
                            ambiguityNotes.Add(desc);
                            continue;
                        }

                        lm.Handled = true;
                        rm.Handled = true;
                        if (lm.Entry.Address != rm.Entry.Address)
                        {
                            var displayImage = rm.Identity.Image ?? lm.Identity.Image ?? rm.Entry.Image ?? lm.Entry.Image;
                            var displayAssembly = rm.Identity.Assembly ?? lm.Identity.Assembly ?? rm.Entry.Assembly ?? lm.Entry.Assembly;
                            var displayMethodSig = rm.Entry.MethodSignature ?? lm.Entry.MethodSignature;
                            addressChanges.Add(new DumpDiffAddressChange
                            {
                                type = lm.Entry.TypeName,
                                image = displayImage,
                                assembly = displayAssembly,
                                method = qKey.MethodName,
                                methodSignature = displayMethodSig,
                                left = $"0x{lm.Entry.Address:X}",
                                right = $"0x{rm.Entry.Address:X}"
                            });
                        }
                    }
                    else
                    {
                        // Overloads within the same qualified type
                        var leftBySig = remLeft
                            .Where(m => !string.IsNullOrEmpty(m.Entry.Signature))
                            .GroupBy(m => m.Entry.Signature!)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        var rightBySig = remRight
                            .Where(m => !string.IsNullOrEmpty(m.Entry.Signature))
                            .GroupBy(m => m.Entry.Signature!)
                            .ToDictionary(g => g.Key, g => g.ToList());

                        var commonSigs = leftBySig.Keys.Intersect(rightBySig.Keys)
                            .OrderBy(s => s, StringComparer.Ordinal);

                        foreach (var sig in commonSigs)
                        {
                            var leftSig = leftBySig[sig];
                            var rightSig = rightBySig[sig];

                            if (leftSig.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)) &&
                                rightSig.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)))
                            {
                                continue;
                            }

                            foreach (var e in leftSig) e.Handled = true;
                            foreach (var e in rightSig) e.Handled = true;

                            if (leftSig.Count == 1 && rightSig.Count == 1)
                            {
                                var lm = leftSig[0];
                                var rm = rightSig[0];

                                if (!string.IsNullOrEmpty(lm.Entry.MethodSignature) && !string.IsNullOrEmpty(rm.Entry.MethodSignature))
                                {
                                    continue;
                                }

                                if (IsSignatureColliding(lm, qKey.MethodName) || IsSignatureColliding(rm, qKey.MethodName))
                                {
                                    result.hasAmbiguousMethodAddresses = true;
                                    var desc = !string.IsNullOrEmpty(sig) ? sig : $"{lm.Entry.TypeName}.{qKey.MethodName}";
                                    ambiguityNotes.Add(desc);
                                }
                                else if (lm.Entry.Address != rm.Entry.Address)
                                {
                                    var displayMethod = !string.IsNullOrEmpty(sig) ? sig : qKey.MethodName;
                                    var displayImage = rm.Identity.Image ?? lm.Identity.Image ?? rm.Entry.Image ?? lm.Entry.Image;
                                    var displayAssembly = rm.Identity.Assembly ?? lm.Identity.Assembly ?? rm.Entry.Assembly ?? lm.Entry.Assembly;
                                    var displayMethodSig = rm.Entry.MethodSignature ?? lm.Entry.MethodSignature;
                                    addressChanges.Add(new DumpDiffAddressChange
                                    {
                                        type = lm.Entry.TypeName,
                                        image = displayImage,
                                        assembly = displayAssembly,
                                        method = displayMethod,
                                        methodSignature = displayMethodSig,
                                        left = $"0x{lm.Entry.Address:X}",
                                        right = $"0x{rm.Entry.Address:X}"
                                    });
                                }
                            }
                            else
                            {
                                var sortedLeft = leftSig.Select(e => e.Entry.Address).OrderBy(a => a).ToList();
                                var sortedRight = rightSig.Select(e => e.Entry.Address).OrderBy(a => a).ToList();

                                if (sortedLeft.Count == sortedRight.Count &&
                                    sortedLeft.Distinct().Count() <= 1 &&
                                    sortedLeft.SequenceEqual(sortedRight))
                                {
                                    // Shared native implementation across all entries: unchanged
                                }
                                else
                                {
                                    result.hasAmbiguousMethodAddresses = true;
                                    var desc = !string.IsNullOrEmpty(sig) ? sig : $"{leftSig[0].Entry.TypeName}.{qKey.MethodName}";
                                    ambiguityNotes.Add(desc);
                                }
                            }
                        }
                    }
                }

                // Phase 2: Reconcile remaining entries by legacy (TypeName, MethodName)
                var leftRemaining = leftItems.Where(x => !x.Handled).ToList();
                var rightRemaining = rightItems.Where(x => !x.Handled).ToList();

                var leftRemainingGroups = leftRemaining
                    .GroupBy(x => (TypeName: x.Entry.TypeName, MethodName: x.Entry.MethodName))
                    .ToDictionary(g => g.Key, g => g.ToList());

                var rightRemainingGroups = rightRemaining
                    .GroupBy(x => (TypeName: x.Entry.TypeName, MethodName: x.Entry.MethodName))
                    .ToDictionary(g => g.Key, g => g.ToList());

                var commonRemainingKeys = leftRemainingGroups.Keys.Intersect(rightRemainingGroups.Keys)
                    .OrderBy(k => k.TypeName, StringComparer.Ordinal)
                    .ThenBy(k => k.MethodName, StringComparer.Ordinal);

                foreach (var remKey in commonRemainingKeys)
                {
                    var leftEntries = leftRemainingGroups[remKey];
                    var rightEntries = rightRemainingGroups[remKey];

                    var leftMatched = new HashSet<MethodDiffItem>();
                    var rightMatched = new HashSet<MethodDiffItem>();

                    // Step 1: Process matches by explicit managed identity (MethodSignature) first
                    var leftByMethodSig = leftEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.MethodSignature))
                        .GroupBy(m => m.Entry.MethodSignature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var rightByMethodSig = rightEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.MethodSignature))
                        .GroupBy(m => m.Entry.MethodSignature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var commonMethodSigs = leftByMethodSig.Keys.Intersect(rightByMethodSig.Keys)
                        .OrderBy(s => s, StringComparer.Ordinal);

                    foreach (var msig in commonMethodSigs)
                    {
                        var leftMs = leftByMethodSig[msig];
                        var rightMs = rightByMethodSig[msig];

                        // Evaluate owner compatibility without materializing Cartesian edge lists
                        var leftMatchCount = new Dictionary<MethodDiffItem, int>(leftMs.Count);
                        var leftSingleMatch = new Dictionary<MethodDiffItem, MethodDiffItem?>(leftMs.Count);
                        foreach (var l in leftMs)
                        {
                            int count = 0;
                            MethodDiffItem? single = null;
                            foreach (var r in rightMs)
                            {
                                if (AreOwnersCompatible(l, r))
                                {
                                    count++;
                                    if (count == 1)
                                    {
                                        single = r;
                                    }
                                    else
                                    {
                                        single = null;
                                        break; // Distinguish 0, 1, or multiple without scanning entire set
                                    }
                                }
                            }
                            leftMatchCount[l] = count;
                            leftSingleMatch[l] = single;
                        }

                        var rightMatchCount = new Dictionary<MethodDiffItem, int>(rightMs.Count);
                        var rightSingleMatch = new Dictionary<MethodDiffItem, MethodDiffItem?>(rightMs.Count);
                        foreach (var r in rightMs)
                        {
                            int count = 0;
                            MethodDiffItem? single = null;
                            foreach (var l in leftMs)
                            {
                                if (AreOwnersCompatible(l, r))
                                {
                                    count++;
                                    if (count == 1)
                                    {
                                        single = l;
                                    }
                                    else
                                    {
                                        single = null;
                                        break; // Stop at multiple
                                    }
                                }
                            }
                            rightMatchCount[r] = count;
                            rightSingleMatch[r] = single;
                        }

                        // Greedily resolve uniquely compatible pairs (1-to-1)
                        var pairedLeft = new HashSet<MethodDiffItem>();
                        var pairedRight = new HashSet<MethodDiffItem>();

                        foreach (var l in leftMs)
                        {
                            if (leftMatchCount[l] == 1)
                            {
                                var r = leftSingleMatch[l]!;
                                if (rightMatchCount[r] == 1 && rightSingleMatch[r] == l && !IsUnitPairIndeterminate(l, r))
                                {
                                    pairedLeft.Add(l);
                                    pairedRight.Add(r);
                                    leftMatched.Add(l);
                                    rightMatched.Add(r);

                                    if (l.Entry.Address != r.Entry.Address)
                                    {
                                        var displayMethod = !string.IsNullOrEmpty(l.Entry.Signature)
                                            ? l.Entry.Signature
                                            : (!string.IsNullOrEmpty(r.Entry.Signature) ? r.Entry.Signature : msig);
                                        var displayImage = r.Identity.Image ?? l.Identity.Image ?? r.Entry.Image ?? l.Entry.Image;
                                        var displayAssembly = r.Identity.Assembly ?? l.Identity.Assembly ?? r.Entry.Assembly ?? l.Entry.Assembly;
                                        var displayMethodSig = r.Entry.MethodSignature ?? l.Entry.MethodSignature;
                                        addressChanges.Add(new DumpDiffAddressChange
                                        {
                                            type = remKey.TypeName,
                                            image = displayImage,
                                            assembly = displayAssembly,
                                            method = displayMethod,
                                            methodSignature = displayMethodSig,
                                            left = $"0x{l.Entry.Address:X}",
                                            right = $"0x{r.Entry.Address:X}"
                                        });
                                    }
                                }
                            }
                        }

                        // Check remaining unpaired entries for shared RVA or ambiguity
                        var unpLeft = leftMs.Where(e => !pairedLeft.Contains(e)).ToList();
                        var unpRight = rightMs.Where(e => !pairedRight.Contains(e)).ToList();

                        var compLeft = unpLeft.Where(l => leftMatchCount[l] > 0).ToList();
                        var compRight = unpRight.Where(r => rightMatchCount[r] > 0).ToList();

                        if (compLeft.Count > 0)
                        {
                            var sharedAddress = compLeft[0].Entry.Address;
                            bool allSameAddress = compLeft.Count == compRight.Count &&
                                compLeft.All(e => e.Entry.Address == sharedAddress) &&
                                compRight.All(e => e.Entry.Address == sharedAddress);

                            bool hasExternalPlausibleCandidate = false;
                            if (allSameAddress)
                            {
                                // Verify if the equal RVA covers all relevant candidates across this group.
                                // If any entry with indeterminate owner still has a compatible candidate
                                // with a different RVA outside this matched set, it cannot be safely collapsed.
                                foreach (var l in compLeft)
                                {
                                    if (l.Identity.IsIndeterminate)
                                    {
                                        foreach (var rOther in rightEntries)
                                        {
                                            if (!pairedRight.Contains(rOther) &&
                                                rOther.Entry.Address != sharedAddress &&
                                                AreOwnersCompatible(l, rOther) &&
                                                (string.IsNullOrEmpty(rOther.Entry.MethodSignature) || string.Equals(l.Entry.MethodSignature, rOther.Entry.MethodSignature, StringComparison.Ordinal)) &&
                                                (string.IsNullOrEmpty(l.Entry.Signature) || string.IsNullOrEmpty(rOther.Entry.Signature) || AreSignaturesCompatible(l, rOther, remKey.MethodName)))
                                            {
                                                hasExternalPlausibleCandidate = true;
                                                break;
                                            }
                                        }
                                    }
                                    if (hasExternalPlausibleCandidate) break;
                                }

                                if (!hasExternalPlausibleCandidate)
                                {
                                    foreach (var r in compRight)
                                    {
                                        if (r.Identity.IsIndeterminate)
                                        {
                                            foreach (var lOther in leftEntries)
                                            {
                                                if (!pairedLeft.Contains(lOther) &&
                                                    lOther.Entry.Address != sharedAddress &&
                                                    AreOwnersCompatible(lOther, r) &&
                                                    (string.IsNullOrEmpty(lOther.Entry.MethodSignature) || string.Equals(r.Entry.MethodSignature, lOther.Entry.MethodSignature, StringComparison.Ordinal)) &&
                                                    (string.IsNullOrEmpty(r.Entry.Signature) || string.IsNullOrEmpty(lOther.Entry.Signature) || AreSignaturesCompatible(lOther, r, remKey.MethodName)))
                                                {
                                                    hasExternalPlausibleCandidate = true;
                                                    break;
                                                }
                                            }
                                        }
                                        if (hasExternalPlausibleCandidate) break;
                                    }
                                }
                            }

                            if (allSameAddress && !hasExternalPlausibleCandidate)
                            {
                                // Shared identical implementation across all relevant compatible candidates: unchanged
                                foreach (var e in compLeft) leftMatched.Add(e);
                                foreach (var e in compRight) rightMatched.Add(e);
                            }
                            else if (!hasExternalPlausibleCandidate)
                            {
                                // Candidates within this managed signature bucket have different RVAs or counts: ambiguous
                                result.hasAmbiguousMethodAddresses = true;
                                ambiguityNotes.Add(msig);
                                foreach (var l in compLeft) leftMatched.Add(l);
                                foreach (var r in compRight) rightMatched.Add(r);
                            }
                            // If hasExternalPlausibleCandidate:
                            // Indeterminate entries have plausible candidates with different RVAs in the legacy phase.
                            // Leave them untouched in leftMatched/rightMatched so they remain available for Step 2 / Step 3.
                        }
                    }

                    // Step 2: Process exact non-empty signature matches for entries without managed match
                    var remLeftEntries = leftEntries.Where(e => !leftMatched.Contains(e)).ToList();
                    var remRightEntries = rightEntries.Where(e => !rightMatched.Contains(e)).ToList();

                    var leftBySig = remLeftEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.Signature))
                        .GroupBy(m => m.Entry.Signature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var rightBySig = remRightEntries
                        .Where(m => !string.IsNullOrEmpty(m.Entry.Signature))
                        .GroupBy(m => m.Entry.Signature!)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    var commonSigs = leftBySig.Keys.Intersect(rightBySig.Keys)
                        .OrderBy(s => s, StringComparer.Ordinal);

                    foreach (var sig in commonSigs)
                    {
                        var leftSig = leftBySig[sig];
                        var rightSig = rightBySig[sig];

                        if (leftSig.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)) &&
                            rightSig.All(e => !string.IsNullOrEmpty(e.Entry.MethodSignature)))
                        {
                            continue;
                        }

                        foreach (var e in leftSig) leftMatched.Add(e);
                        foreach (var e in rightSig) rightMatched.Add(e);

                        if (leftSig.Count == 1 && rightSig.Count == 1)
                        {
                            var l = leftSig[0];
                            var r = rightSig[0];

                            if (!string.IsNullOrEmpty(l.Entry.MethodSignature) && !string.IsNullOrEmpty(r.Entry.MethodSignature))
                            {
                                continue;
                            }

                            if (IsSignatureColliding(l, remKey.MethodName) || IsSignatureColliding(r, remKey.MethodName))
                            {
                                result.hasAmbiguousMethodAddresses = true;
                                ambiguityNotes.Add(sig);
                            }
                            else
                            {
                                bool isIndeterminate = IsUnitPairIndeterminate(l, r);
                                if (isIndeterminate)
                                {
                                    result.hasAmbiguousMethodAddresses = true;
                                    ambiguityNotes.Add(sig);
                                }
                                else if (AreOwnersCompatible(l, r))
                                {
                                    if (l.Entry.Address != r.Entry.Address)
                                    {
                                        var displayImage = r.Identity.Image ?? l.Identity.Image ?? r.Entry.Image ?? l.Entry.Image;
                                        var displayAssembly = r.Identity.Assembly ?? l.Identity.Assembly ?? r.Entry.Assembly ?? l.Entry.Assembly;
                                        var displayMethodSig = r.Entry.MethodSignature ?? l.Entry.MethodSignature;
                                        addressChanges.Add(new DumpDiffAddressChange
                                        {
                                            type = remKey.TypeName,
                                            image = displayImage,
                                            assembly = displayAssembly,
                                            method = sig,
                                            methodSignature = displayMethodSig,
                                            left = $"0x{l.Entry.Address:X}",
                                            right = $"0x{r.Entry.Address:X}"
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Multiple candidates with the exact same non-empty signature
                            var sortedLeft = leftSig.Select(e => e.Entry.Address).OrderBy(a => a).ToList();
                            var sortedRight = rightSig.Select(e => e.Entry.Address).OrderBy(a => a).ToList();

                            if (sortedLeft.Count == sortedRight.Count &&
                                sortedLeft.Distinct().Count() <= 1 &&
                                sortedLeft.SequenceEqual(sortedRight))
                            {
                                // Shared identical implementation across all entries: unchanged
                                continue;
                            }

                            bool hasCompatiblePair = false;
                            foreach (var l in leftSig)
                            {
                                foreach (var r in rightSig)
                                {
                                    if (AreOwnersCompatible(l, r))
                                    {
                                        hasCompatiblePair = true;
                                        break;
                                    }
                                }
                                if (hasCompatiblePair) break;
                            }

                            if (!hasCompatiblePair)
                            {
                                // Disjoint owners: zero compatible pairings exist.
                                // They remain unmatched without triggering false ambiguity.
                                continue;
                            }

                            result.hasAmbiguousMethodAddresses = true;
                            ambiguityNotes.Add(sig);
                        }
                    }

                    // Step 3: Process remaining entries (treating absent signature as unknown information)
                    var remainingLeft = leftEntries.Where(e => !leftMatched.Contains(e)).ToList();
                    var remainingRight = rightEntries.Where(e => !rightMatched.Contains(e)).ToList();

                    if (remainingLeft.Count == 0 || remainingRight.Count == 0)
                    {
                        continue;
                    }

                    if (remainingLeft.Count == 1 && remainingRight.Count == 1)
                    {
                        var l = remainingLeft[0];
                        var r = remainingRight[0];

                        if (!string.IsNullOrEmpty(l.Entry.MethodSignature) && !string.IsNullOrEmpty(r.Entry.MethodSignature))
                        {
                            continue; // Incompatible explicit managed signatures
                        }

                        var leftHasSig = !string.IsNullOrEmpty(l.Entry.Signature);
                        var rightHasSig = !string.IsNullOrEmpty(r.Entry.Signature);

                        if (leftHasSig && rightHasSig && !AreSignaturesCompatible(l, r, remKey.MethodName))
                        {
                            // Distinct overloads with different signatures: do not pair
                            continue;
                        }

                        if (IsSignatureColliding(l, remKey.MethodName) || IsSignatureColliding(r, remKey.MethodName))
                        {
                            result.hasAmbiguousMethodAddresses = true;
                            var desc = rightHasSig ? r.Entry.Signature! : (leftHasSig ? l.Entry.Signature! : remKey.MethodName);
                            ambiguityNotes.Add(desc);
                            continue;
                        }

                        // At least one lacks signature, or both signatures are compatible: uniquely identifiable pair
                        bool isIndeterminate = IsUnitPairIndeterminate(l, r);
                        if (isIndeterminate)
                        {
                            result.hasAmbiguousMethodAddresses = true;
                            var desc = rightHasSig ? r.Entry.Signature! : (leftHasSig ? l.Entry.Signature! : remKey.MethodName);
                            ambiguityNotes.Add(desc);
                        }
                        else if (AreOwnersCompatible(l, r))
                        {
                            if (l.Entry.Address != r.Entry.Address)
                            {
                                var displayMethod = rightHasSig ? r.Entry.Signature! : (leftHasSig ? l.Entry.Signature! : remKey.MethodName);
                                var displayImage = r.Identity.Image ?? l.Identity.Image ?? r.Entry.Image ?? l.Entry.Image;
                                var displayAssembly = r.Identity.Assembly ?? l.Identity.Assembly ?? r.Entry.Assembly ?? l.Entry.Assembly;
                                var displayMethodSig = r.Entry.MethodSignature ?? l.Entry.MethodSignature;
                                addressChanges.Add(new DumpDiffAddressChange
                                {
                                    type = remKey.TypeName,
                                    image = displayImage,
                                    assembly = displayAssembly,
                                    method = displayMethod,
                                    methodSignature = displayMethodSig,
                                    left = $"0x{l.Entry.Address:X}",
                                    right = $"0x{r.Entry.Address:X}"
                                });
                            }
                        }
                    }
                    else
                    {
                        // Multiple competing candidates remaining
                        // First: check shared identical implementation across all remaining entries
                        var sortedLeft = remainingLeft.Select(e => e.Entry.Address).OrderBy(a => a).ToList();
                        var sortedRight = remainingRight.Select(e => e.Entry.Address).OrderBy(a => a).ToList();

                        if (sortedLeft.Count == sortedRight.Count &&
                            sortedLeft.Distinct().Count() <= 1 &&
                            sortedLeft.SequenceEqual(sortedRight))
                        {
                            // Shared identical implementation across all entries: unchanged
                            continue;
                        }

                        // Check if any compatible candidate pair exists (considering signature and owner)
                        bool hasCompatiblePair = false;
                        foreach (var l in remainingLeft)
                        {
                            foreach (var r in remainingRight)
                            {
                                if (AreEntriesCompatible(l, r, remKey.MethodName))
                                {
                                    hasCompatiblePair = true;
                                    break;
                                }
                            }
                            if (hasCompatiblePair) break;
                        }

                        if (!hasCompatiblePair)
                        {
                            // Zero compatible pairings exist (disjoint owners and/or distinct known overloads).
                            // They remain unmatched without triggering false ambiguity.
                            continue;
                        }

                        result.hasAmbiguousMethodAddresses = true;
                        var desc = !string.IsNullOrEmpty(remKey.TypeName) ? $"{remKey.TypeName}.{remKey.MethodName}" : remKey.MethodName;
                        ambiguityNotes.Add(desc);
                    }
                }

                result.methods.addressChanged = addressChanges
                    .OrderBy(x => x.type, StringComparer.Ordinal)
                    .ThenBy(x => x.method, StringComparer.Ordinal)
                    .ToList();

                if (ambiguityNotes.Count > 0)
                {
                    var distinctNotes = ambiguityNotes.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal);
                    result.methods.note = $"Address comparison has unresolved ambiguous overloads: unable to correlate {string.Join("; ", distinctNotes)}";
                }
                else
                {
                    result.methods.note = null;
                }
            }
            else
            {
                result.methods.addressChanged = null;
                if (leftArtifacts.ScriptMethods == null && rightArtifacts.ScriptMethods == null)
                {
                    result.methods.note = "Address comparison omitted: script.json missing on both left and right";
                }
                else if (leftArtifacts.ScriptMethods == null)
                {
                    result.methods.note = "Address comparison omitted: script.json missing on left";
                }
                else
                {
                    result.methods.note = "Address comparison omitted: script.json missing on right";
                }
            }

            // 3. Compare Literals
            if (leftArtifacts.Literals != null && rightArtifacts.Literals != null)
            {
                var leftLit = leftArtifacts.Literals;
                var rightLit = rightArtifacts.Literals;

                var addedLit = rightLit.Keys.Except(leftLit.Keys)
                    .OrderBy(x => x)
                    .Select(idx => new DumpDiffLiteralAdded { index = idx, value = rightLit[idx] })
                    .ToList();

                var changedLit = leftLit.Keys.Intersect(rightLit.Keys)
                    .OrderBy(x => x)
                    .Where(idx => leftLit[idx] != rightLit[idx])
                    .Select(idx => new DumpDiffLiteralChanged { index = idx, left = leftLit[idx], right = rightLit[idx] })
                    .ToList();

                result.literals.addedByIndex = addedLit;
                result.literals.valueChangedByIndex = changedLit;
            }
            else
            {
                result.literals.addedByIndex = new List<DumpDiffLiteralAdded>();
                result.literals.valueChangedByIndex = new List<DumpDiffLiteralChanged>();
                if (leftArtifacts.Literals == null && rightArtifacts.Literals == null)
                {
                    result.literals.note = "Literal comparison omitted: stringliteral-indexed.json missing on both left and right";
                }
                else if (leftArtifacts.Literals == null)
                {
                    result.literals.note = "Literal comparison omitted: stringliteral-indexed.json missing on left";
                }
                else
                {
                    result.literals.note = "Literal comparison omitted: stringliteral-indexed.json missing on right";
                }
            }

            // 4. Summary
            result.summary.addedTypes = result.types.added.Count;
            result.summary.removedTypes = result.types.removed.Count;
            result.summary.addressChanges = result.methods.addressChanged?.Count ?? 0;
            result.summary.valueChanges = result.literals.valueChangedByIndex.Count;

            return result;
        }

        private static string FormatParams(JsonDumpMethod m)
        {
            if (m.parameters == null || m.parameters.Count == 0)
                return string.Empty;
            return string.Join(", ", m.parameters.Select(p => $"{p.type} {p.name}".Trim()));
        }

        private static string FormatSignature(JsonDumpMethod m)
        {
            var pars = m.parameters != null ? string.Join(", ", m.parameters.Select(p => p.type)) : "";
            return $"{m.returnType} {m.name}({pars})";
        }

        private static string ComputeStreamSha256(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: false);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.AppendData(buffer, 0, bytesRead);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        public class ScriptMethodEntry
        {
            public string TypeName { get; set; } = string.Empty;
            public string MethodName { get; set; } = string.Empty;
            public string Signature { get; set; } = string.Empty;
            public string? MethodSignature { get; set; }
            public ulong Address { get; set; }
            public string? Image { get; set; }
            public string? Assembly { get; set; }

            public string? PreferredScope => !string.IsNullOrEmpty(Image) ? Image : Assembly;
            public string FormatTypeKey() => JsonDumpType.FormatKey(Image, Assembly, TypeName);
        }

        private class ResolvedMethodIdentity
        {
            public string? QualifiedKey { get; set; }
            public string? Image { get; set; }
            public string? Assembly { get; set; }
            public bool IsAttributed { get; set; }
            public bool IsIndeterminate { get; set; }
            public JsonDumpType? DeclaringType { get; set; }
        }

        private class MethodDiffItem
        {
            public ScriptMethodEntry Entry { get; set; } = null!;
            public ResolvedMethodIdentity Identity { get; set; } = null!;
            public bool Handled { get; set; }
        }

        private static bool AreSignaturesCompatible(MethodDiffItem lm, MethodDiffItem rm, string methodName)
        {
            if (!string.IsNullOrEmpty(lm.Entry.MethodSignature) && !string.IsNullOrEmpty(rm.Entry.MethodSignature))
            {
                return string.Equals(lm.Entry.MethodSignature, rm.Entry.MethodSignature, StringComparison.Ordinal);
            }

            var lSig = lm.Entry.Signature ?? "";
            var rSig = rm.Entry.Signature ?? "";

            if (string.Equals(lSig, rSig, StringComparison.Ordinal))
                return true;

            // Check managed declaration in dump.json
            var lDeclType = lm.Identity.DeclaringType;
            var rDeclType = rm.Identity.DeclaringType;
            if (lDeclType?.methods != null && rDeclType?.methods != null)
            {
                var lMethods = lDeclType.methods.Where(m => m.name == methodName).ToList();
                var rMethods = rDeclType.methods.Where(m => m.name == methodName).ToList();
                if (lMethods.Count == 1 && rMethods.Count == 1)
                {
                    var lTypes = string.Join(", ", lMethods[0].parameters?.Select(p => p.type) ?? Array.Empty<string>());
                    var rTypes = string.Join(", ", rMethods[0].parameters?.Select(p => p.type) ?? Array.Empty<string>());
                    if (string.Equals(lTypes, rTypes, StringComparison.Ordinal) &&
                        string.Equals(lMethods[0].returnType, rMethods[0].returnType, StringComparison.Ordinal))
                    {
                        return true;
                    }
                    return false;
                }
            }

            // Fallback: strip parameter names from C signature
            var lNorm = StripParameterNamesFromSignature(lSig);
            var rNorm = StripParameterNamesFromSignature(rSig);
            return string.Equals(lNorm, rNorm, StringComparison.Ordinal);
        }

        private static string StripParameterNamesFromSignature(string sig)
        {
            if (string.IsNullOrEmpty(sig)) return "";
            var openParen = sig.IndexOf('(');
            var closeParen = sig.LastIndexOf(')');
            if (openParen < 0 || closeParen <= openParen)
                return sig;

            var prefix = sig.Substring(0, openParen);
            var paramStr = sig.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            if (string.IsNullOrEmpty(paramStr))
                return $"{prefix}()";

            // Split parameters by commas outside of angle brackets <...> and square brackets [...]
            var paramList = new List<string>();
            int depth = 0;
            int bracketDepth = 0;
            int lastStart = 0;
            for (int i = 0; i < paramStr.Length; i++)
            {
                char c = paramStr[i];
                if (c == '<') depth++;
                else if (c == '>') { if (depth > 0) depth--; }
                else if (c == '[') bracketDepth++;
                else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if (c == ',' && depth == 0 && bracketDepth == 0)
                {
                    paramList.Add(paramStr.Substring(lastStart, i - lastStart).Trim());
                    lastStart = i + 1;
                }
            }
            if (lastStart < paramStr.Length)
            {
                paramList.Add(paramStr.Substring(lastStart).Trim());
            }

            var normalizedParams = new List<string>();
            foreach (var p in paramList)
            {
                var trimmed = p.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var parts = trimmed.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    var last = parts[^1];
                    if (!last.EndsWith('*') && !last.EndsWith('&'))
                    {
                        normalizedParams.Add(string.Join(" ", parts.Take(parts.Length - 1)));
                        continue;
                    }
                }
                normalizedParams.Add(trimmed);
            }

            return $"{prefix}({string.Join(", ", normalizedParams)})";
        }

        private static bool IsUnitPairIndeterminate(MethodDiffItem l, MethodDiffItem r)
        {
            return l.Identity.IsIndeterminate || r.Identity.IsIndeterminate;
        }

        private static bool AreOwnersCompatible(MethodDiffItem left, MethodDiffItem right)
        {
            var leftImg = left.Identity.Image ?? left.Entry.Image;
            var rightImg = right.Identity.Image ?? right.Entry.Image;
            if (!string.IsNullOrEmpty(leftImg) && !string.IsNullOrEmpty(rightImg))
            {
                return string.Equals(leftImg, rightImg, StringComparison.Ordinal);
            }

            var leftAsm = left.Identity.Assembly ?? left.Entry.Assembly;
            var rightAsm = right.Identity.Assembly ?? right.Entry.Assembly;
            if (!string.IsNullOrEmpty(leftAsm) && !string.IsNullOrEmpty(rightAsm))
            {
                return string.Equals(leftAsm, rightAsm, StringComparison.Ordinal);
            }

            return true;
        }

        private static bool AreEntriesCompatible(MethodDiffItem l, MethodDiffItem r, string methodName)
        {
            if (!AreOwnersCompatible(l, r))
                return false;

            var lHasSig = !string.IsNullOrEmpty(l.Entry.Signature);
            var rHasSig = !string.IsNullOrEmpty(r.Entry.Signature);
            if (lHasSig && rHasSig && !AreSignaturesCompatible(l, r, methodName))
                return false;

            return true;
        }

        private class CParamInfo
        {
            public string Type { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        private static bool IsSignatureColliding(MethodDiffItem item, string methodName)
        {
            if (!string.IsNullOrEmpty(item.Entry.MethodSignature))
            {
                // Explicit managed signature distinguishes overloads definitively
                return false;
            }

            var declType = item.Identity.DeclaringType;
            if (declType?.methods == null) return false;

            var candidates = declType.methods
                .Where(m => string.Equals(m.name, methodName, StringComparison.Ordinal))
                .ToList();

            if (candidates.Count <= 1) return false;

            var sig = item.Entry.Signature;
            if (string.IsNullOrEmpty(sig))
            {
                // No signature to distinguish among multiple declarations: colliding
                return true;
            }

            var cParams = ExtractManagedParametersFromSignature(sig);
            int matchCount = 0;
            foreach (var cand in candidates)
            {
                if (DoesDeclarationMatchSignature(cand, cParams))
                {
                    matchCount++;
                }
            }

            return matchCount > 1;
        }

        private static bool DoesDeclarationMatchSignature(JsonDumpMethod cand, List<CParamInfo> cParams)
        {
            var candParams = cand.parameters ?? new List<JsonDumpParameter>();
            if (candParams.Count != cParams.Count)
            {
                return false;
            }

            for (int i = 0; i < cParams.Count; i++)
            {
                if (!AreParameterTypesMatching(candParams[i].type, cParams[i].Type))
                    return false;
            }

            return true;
        }

        private static bool AreParameterTypesMatching(string managedType, string cType)
        {
            if (string.Equals(managedType, cType, StringComparison.OrdinalIgnoreCase))
                return true;

            cType = cType.Trim();
            if (cType.StartsWith("const "))
                cType = cType.Substring(6).Trim();
            if (cType.StartsWith("struct "))
                cType = cType.Substring(7).Trim();

            var managedSimple = managedType;
            if (managedSimple.StartsWith("System."))
                managedSimple = managedSimple.Substring(7);

            if (string.Equals(managedSimple, cType, StringComparison.OrdinalIgnoreCase))
                return true;

            var m = managedSimple.ToLowerInvariant();
            var c = cType.ToLowerInvariant();

            if (m == c) return true;

            if ((m == "char" || m == "ushort" || m == "uint16") && (c == "uint16_t" || c == "ushort" || c == "char" || c == "il2cppchar"))
                return true;

            if ((m == "short" || m == "int16") && (c == "int16_t" || c == "short"))
                return true;

            if ((m == "int" || m == "int32") && (c == "int32_t" || c == "int"))
                return true;

            if ((m == "uint" || m == "uint32") && (c == "uint32_t" || c == "uint"))
                return true;

            if ((m == "long" || m == "int64") && (c == "int64_t" || c == "long"))
                return true;

            if ((m == "ulong" || m == "uint64") && (c == "uint64_t" || c == "ulong"))
                return true;

            if ((m == "byte" || m == "uint8") && (c == "uint8_t" || c == "byte"))
                return true;

            if ((m == "sbyte" || m == "int8") && (c == "int8_t" || c == "sbyte"))
                return true;

            if (m == "bool" || m == "boolean")
                return c == "bool" || c == "boolean";

            if (m == "single" || m == "float")
                return c == "float" || c == "single";

            if (m == "double")
                return c == "double";

            if (m == "intptr" && (c == "intptr_t" || c == "void*"))
                return true;

            if (m == "uintptr" && (c == "uintptr_t" || c == "void*"))
                return true;

            bool isManagedPrimitive = (m == "char" || m == "ushort" || m == "short" || m == "int" || m == "uint" ||
                                       m == "long" || m == "ulong" || m == "byte" || m == "sbyte" || m == "bool" ||
                                       m == "float" || m == "double" || m == "int32" || m == "uint32" || m == "int16" ||
                                       m == "uint16" || m == "int64" || m == "uint64" || m == "single" || m == "boolean");

            if (isManagedPrimitive && c.EndsWith("*"))
            {
                return false;
            }

            if (m == "string")
            {
                return c.Contains("string") || c == "il2cppstring*" || c == "system_string_o*";
            }

            if (m == "type")
            {
                return c.Contains("type") || c == "il2cppreflectiontype*" || c == "system_type_o*";
            }

            if (m == "object")
            {
                return c.Contains("object") || c == "il2cppobject*";
            }

            if (managedType.EndsWith("[]"))
            {
                var elemType = managedType.Substring(0, managedType.Length - 2);
                return c.Contains("array") || c == "il2cpparray*" || c.Contains(elemType.ToLowerInvariant());
            }

            if (c.EndsWith("*"))
            {
                var cBase = c.TrimEnd('*').Trim();
                if (cBase.EndsWith("_o"))
                    cBase = cBase.Substring(0, cBase.Length - 2);

                if (cBase.EndsWith(m, StringComparison.OrdinalIgnoreCase) ||
                    cBase.Replace("_", ".").EndsWith(m, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<CParamInfo> ExtractManagedParametersFromSignature(string sig)
        {
            var rawParams = ExtractRawParameters(sig);
            if (rawParams == null || rawParams.Count == 0)
                return new List<CParamInfo>();

            var list = new List<CParamInfo>(rawParams);

            // 1. Remove receptor (__this) if first parameter
            if (list.Count > 0)
            {
                var first = list[0];
                if (first.Name == "__this" || first.Name.StartsWith("__this") || first.Type.EndsWith("__this"))
                {
                    list.RemoveAt(0);
                }
            }

            // 2. Remove auxiliary metadata argument (const MethodInfo* method) if last parameter
            if (list.Count > 0)
            {
                var last = list[^1];
                if (last.Name == "method" || last.Type.Contains("MethodInfo") || last.Type.EndsWith("MethodInfo*"))
                {
                    list.RemoveAt(list.Count - 1);
                }
            }

            return list;
        }

        private static List<CParamInfo>? ExtractRawParameters(string sig)
        {
            if (string.IsNullOrEmpty(sig)) return null;
            var openParen = sig.IndexOf('(');
            var closeParen = sig.LastIndexOf(')');
            if (openParen < 0 || closeParen <= openParen)
                return null;

            var paramStr = sig.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            if (string.IsNullOrEmpty(paramStr))
                return new List<CParamInfo>();

            var paramTokens = new List<string>();
            int depth = 0;
            int bracketDepth = 0;
            int lastStart = 0;
            for (int i = 0; i < paramStr.Length; i++)
            {
                char c = paramStr[i];
                if (c == '<') depth++;
                else if (c == '>') { if (depth > 0) depth--; }
                else if (c == '[') bracketDepth++;
                else if (c == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if (c == ',' && depth == 0 && bracketDepth == 0)
                {
                    paramTokens.Add(paramStr.Substring(lastStart, i - lastStart).Trim());
                    lastStart = i + 1;
                }
            }
            if (lastStart < paramStr.Length)
            {
                paramTokens.Add(paramStr.Substring(lastStart).Trim());
            }

            var result = new List<CParamInfo>();
            foreach (var token in paramTokens)
            {
                var trimmed = token.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed == "void") continue;

                var parts = trimmed.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    result.Add(new CParamInfo { Type = parts[0], Name = "" });
                }
                else
                {
                    var name = parts[^1];
                    if (name.EndsWith('*') || name.EndsWith('&'))
                    {
                        result.Add(new CParamInfo { Type = trimmed, Name = "" });
                    }
                    else
                    {
                        var type = string.Join(" ", parts.Take(parts.Length - 1));
                        result.Add(new CParamInfo { Type = type, Name = name });
                    }
                }
            }
            return result;
        }

        private class TypeLookupIndex
        {
            private readonly Dictionary<(string Image, string FullName), List<ReconciledType>> _byImageAndName = new();
            private readonly Dictionary<(string Assembly, string FullName), List<ReconciledType>> _byAssemblyAndName = new();
            private readonly Dictionary<string, List<ReconciledType>> _byFullName = new();

            private readonly Dictionary<(string Image, string BaseName, int Arity), List<ReconciledType>> _byImageBaseGeneric = new();
            private readonly Dictionary<(string Assembly, string BaseName, int Arity), List<ReconciledType>> _byAssemblyBaseGeneric = new();
            private readonly Dictionary<(string BaseName, int Arity), List<ReconciledType>> _byBaseGeneric = new();

            private readonly HashSet<string> _images = new(StringComparer.Ordinal);
            private readonly HashSet<string> _assemblies = new(StringComparer.Ordinal);

            public static TypeLookupIndex Build(List<ReconciledType> reconciled, bool isLeft)
            {
                var index = new TypeLookupIndex();
                foreach (var r in reconciled)
                {
                    var target = isLeft ? r.Left : r.Right;
                    if (target == null) continue;

                    var fullName = r.FullName;
                    var image = target.image ?? r.Image;
                    var assembly = target.assembly ?? r.Assembly;

                    AddToList(index._byFullName, fullName, r);

                    if (!string.IsNullOrEmpty(image))
                    {
                        index._images.Add(image);
                        AddToList(index._byImageAndName, (image, fullName), r);
                    }

                    if (!string.IsNullOrEmpty(assembly))
                    {
                        index._assemblies.Add(assembly);
                        AddToList(index._byAssemblyAndName, (assembly, fullName), r);
                    }

                    if (TryExtractGenericBase(fullName, out var baseName, out var arity))
                    {
                        AddToList(index._byBaseGeneric, (baseName, arity), r);
                        if (!string.IsNullOrEmpty(image))
                        {
                            AddToList(index._byImageBaseGeneric, (image, baseName, arity), r);
                        }
                        if (!string.IsNullOrEmpty(assembly))
                        {
                            AddToList(index._byAssemblyBaseGeneric, (assembly, baseName, arity), r);
                        }
                    }
                }
                return index;
            }

            private static void AddToList<TKey>(Dictionary<TKey, List<ReconciledType>> dict, TKey key, ReconciledType item) where TKey : notnull
            {
                if (!dict.TryGetValue(key, out var list))
                {
                    list = new List<ReconciledType>();
                    dict[key] = list;
                }
                list.Add(item);
            }

            internal static bool TryExtractGenericBase(string typeName, out string baseName, out int arity)
            {
                baseName = string.Empty;
                arity = 0;
                if (string.IsNullOrEmpty(typeName) || !typeName.Contains('<'))
                    return false;

                var sb = new StringBuilder();
                int totalArity = 0;
                int depth = 0;
                int bracketDepth = 0;
                int blockCommas = 0;
                bool inBlock = false;

                for (int i = 0; i < typeName.Length; i++)
                {
                    char c = typeName[i];
                    if (c == '<')
                    {
                        if (depth == 0)
                        {
                            inBlock = true;
                            blockCommas = 0;
                        }
                        depth++;
                    }
                    else if (c == '>')
                    {
                        depth--;
                        if (depth == 0 && inBlock)
                        {
                            totalArity += blockCommas + 1;
                            inBlock = false;
                        }
                        else if (depth < 0)
                        {
                            depth = 0;
                        }
                    }
                    else if (c == '[')
                    {
                        bracketDepth++;
                    }
                    else if (c == ']')
                    {
                        if (bracketDepth > 0) bracketDepth--;
                    }
                    else if (c == ',' && depth == 1 && bracketDepth == 0)
                    {
                        blockCommas++;
                    }
                    else if (depth == 0)
                    {
                        sb.Append(c);
                    }
                }

                if (depth != 0 || totalArity == 0)
                    return false;

                baseName = sb.ToString();
                arity = totalArity;
                return true;
            }

            public ResolvedMethodIdentity Resolve(ScriptMethodEntry entry, bool isLeft)
            {
                var identity = new ResolvedMethodIdentity();
                bool hasGeneric = TryExtractGenericBase(entry.TypeName, out var baseName, out var arity);

                if (!string.IsNullOrEmpty(entry.Image))
                {
                    List<ReconciledType>? matches = null;
                    if (!_byImageAndName.TryGetValue((entry.Image, entry.TypeName), out matches) && hasGeneric)
                    {
                        _byImageBaseGeneric.TryGetValue((entry.Image, baseName, arity), out matches);
                    }

                    if (matches != null && matches.Count == 1)
                    {
                        var m = matches[0];
                        identity.DeclaringType = isLeft ? m.Left : m.Right;
                        identity.Image = m.Image ?? (isLeft ? m.Left?.image : m.Right?.image) ?? entry.Image;
                        identity.Assembly = m.Assembly ?? (isLeft ? m.Left?.assembly : m.Right?.assembly) ?? entry.Assembly;
                        // Preserve concrete exported type name in qualified key
                        identity.QualifiedKey = JsonDumpType.FormatKey(identity.Image, identity.Assembly, entry.TypeName);
                        identity.IsAttributed = true;
                        return identity;
                    }

                    if (matches == null || matches.Count == 0)
                    {
                        identity.Image = entry.Image;
                        identity.Assembly = entry.Assembly;
                        identity.QualifiedKey = JsonDumpType.FormatKey(entry.Image, entry.Assembly, entry.TypeName);
                        identity.IsAttributed = true;
                        return identity;
                    }

                    identity.Image = entry.Image;
                    identity.Assembly = entry.Assembly;
                    identity.QualifiedKey = null;
                    identity.IsAttributed = false;
                    identity.IsIndeterminate = matches != null && matches.Count > 1;
                    return identity;
                }

                if (!string.IsNullOrEmpty(entry.Assembly))
                {
                    List<ReconciledType>? matches = null;
                    if (!_byAssemblyAndName.TryGetValue((entry.Assembly, entry.TypeName), out matches) && hasGeneric)
                    {
                        _byAssemblyBaseGeneric.TryGetValue((entry.Assembly, baseName, arity), out matches);
                    }

                    if (matches != null && matches.Count == 1)
                    {
                        var m = matches[0];
                        identity.DeclaringType = isLeft ? m.Left : m.Right;
                        identity.Image = m.Image ?? (isLeft ? m.Left?.image : m.Right?.image);
                        identity.Assembly = m.Assembly ?? (isLeft ? m.Left?.assembly : m.Right?.assembly) ?? entry.Assembly;
                        identity.QualifiedKey = JsonDumpType.FormatKey(identity.Image, identity.Assembly, entry.TypeName);
                        identity.IsAttributed = true;
                        return identity;
                    }

                    // Ambiguous candidates in this assembly
                    identity.Image = null;
                    identity.Assembly = entry.Assembly;
                    identity.QualifiedKey = null;
                    identity.IsAttributed = false;
                    identity.IsIndeterminate = matches != null && matches.Count > 1;
                    return identity;
                }

                // Legacy: no Image or Assembly
                List<ReconciledType>? nameMatches = null;
                if (!_byFullName.TryGetValue(entry.TypeName, out nameMatches) && hasGeneric)
                {
                    _byBaseGeneric.TryGetValue((baseName, arity), out nameMatches);
                }

                if (nameMatches != null && nameMatches.Count == 1)
                {
                    var m = nameMatches[0];
                    identity.DeclaringType = isLeft ? m.Left : m.Right;
                    identity.Image = m.Image ?? (isLeft ? m.Left?.image : m.Right?.image);
                    identity.Assembly = m.Assembly ?? (isLeft ? m.Left?.assembly : m.Right?.assembly);
                    identity.QualifiedKey = JsonDumpType.FormatKey(identity.Image, identity.Assembly, entry.TypeName);
                    identity.IsAttributed = true;
                    return identity;
                }

                identity.Image = null;
                identity.Assembly = null;
                identity.QualifiedKey = null;
                identity.IsAttributed = false;
                identity.IsIndeterminate = (nameMatches != null && nameMatches.Count > 1) ||
                                           (hasGeneric && (_images.Count > 1 || _assemblies.Count > 1));
                return identity;
            }
        }

        private class ParsedArtifacts
        {
            public DumpDiffEndpoint Endpoint { get; set; } = new();
            public JsonDumpModel Model { get; set; } = new();
            public List<ScriptMethodEntry>? ScriptMethods { get; set; }
            public Dictionary<uint, string>? Literals { get; set; }
        }

        private static ParsedArtifacts ResolveArtifacts(string inputPath, string label)
        {
            string dir;
            string dumpJsonPath;

            if (Directory.Exists(inputPath))
            {
                dir = Path.GetFullPath(inputPath);
                dumpJsonPath = Path.Combine(dir, "dump.json");
            }
            else if (File.Exists(inputPath))
            {
                dumpJsonPath = Path.GetFullPath(inputPath);
                dir = Path.GetDirectoryName(dumpJsonPath)!;
            }
            else
            {
                throw new FileNotFoundException($"Input {label} path not found: {inputPath}");
            }

            if (!File.Exists(dumpJsonPath))
            {
                throw new FileNotFoundException($"dump.json not found in {label} path: {dumpJsonPath}");
            }

            var artifacts = new ParsedArtifacts();

            // 1. Endpoint Source and SHA-256
            var sidecarPath = Path.Combine(dir, "dump-metadata.json");
            var sidecarFound = false;
            if (File.Exists(sidecarPath))
            {
                try
                {
                    using var stream = File.OpenRead(sidecarPath);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("inputs", out var inputs) &&
                        inputs.TryGetProperty("il2cpp", out var il2cpp))
                    {
                        var il2cppSha = il2cpp.TryGetProperty("sha256", out var shaEl) ? shaEl.GetString() : null;
                        var il2cppSource = il2cpp.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                        if (!string.IsNullOrEmpty(il2cppSha))
                        {
                            artifacts.Endpoint.source = il2cppSource ?? dir;
                            artifacts.Endpoint.sha256 = il2cppSha;
                            sidecarFound = true;
                        }
                    }
                }
                catch
                {
                    // Fall back to computing dump.json hash
                }
            }

            if (!sidecarFound)
            {
                artifacts.Endpoint.source = dumpJsonPath;
                artifacts.Endpoint.sha256 = ComputeStreamSha256(dumpJsonPath);
            }

            // 2. Parse dump.json (required input, must have 'types' array)
            using (var stream = File.OpenRead(dumpJsonPath))
            {
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("types", out var typesEl) ||
                    typesEl.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"Invalid {label} dump.json: missing 'types' array property.");
                }

                try
                {
                    artifacts.Model = JsonSerializer.Deserialize<JsonDumpModel>(doc.RootElement.GetRawText())
                        ?? throw new InvalidDataException($"Invalid {label} dump.json: missing 'types' array property.");
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Failed to deserialize {label} dump.json: {ex.Message}", ex);
                }
            }

            // 3. Parse script.json (optional, but if present must be valid)
            var scriptPath = Path.Combine(dir, "script.json");
            if (File.Exists(scriptPath))
            {
                try
                {
                    using var stream = File.OpenRead(scriptPath);
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("ScriptMethod", out var scriptMethods) ||
                        scriptMethods.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException($"Failed to parse {label} script.json: missing 'ScriptMethod' array.");
                    }

                    var list = new List<ScriptMethodEntry>();
                    foreach (var item in scriptMethods.EnumerateArray())
                    {
                        if (!item.TryGetProperty("Name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item is missing required string property 'Name'.");
                        }
                        var name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name))
                        {
                            throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item has empty 'Name'.");
                        }

                        if (!item.TryGetProperty("Address", out var addrEl))
                        {
                            throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item '{name}' is missing required property 'Address'.");
                        }

                        ulong addr;
                        if (addrEl.ValueKind == JsonValueKind.Number)
                        {
                            if (!addrEl.TryGetUInt64(out addr))
                            {
                                throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item '{name}' has invalid numeric Address.");
                            }
                        }
                        else if (addrEl.ValueKind == JsonValueKind.String)
                        {
                            var str = addrEl.GetString();
                            if (string.IsNullOrWhiteSpace(str))
                            {
                                throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item '{name}' has empty Address string.");
                            }

                            bool parsed;
                            if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                            {
                                parsed = ulong.TryParse(str.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out addr);
                            }
                            else
                            {
                                parsed = ulong.TryParse(str, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out addr);
                            }

                            if (!parsed)
                            {
                                throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item '{name}' has invalid Address format '{str}'.");
                            }
                        }
                        else
                        {
                            throw new InvalidDataException($"Failed to parse {label} script.json: ScriptMethod item '{name}' has incompatible Address type '{addrEl.ValueKind}'.");
                        }

                        var sig = item.TryGetProperty("Signature", out var sigEl) && sigEl.ValueKind == JsonValueKind.String
                            ? (sigEl.GetString() ?? "")
                            : "";

                        var sep = name.IndexOf("$$");
                        string typeName;
                        string methodName;
                        if (sep >= 0)
                        {
                            typeName = name.Substring(0, sep);
                            methodName = name.Substring(sep + 2);
                        }
                        else
                        {
                            typeName = "";
                            methodName = name;
                        }

                        list.Add(new ScriptMethodEntry
                        {
                            TypeName = typeName,
                            MethodName = methodName,
                            Signature = sig,
                            MethodSignature = item.TryGetProperty("MethodSignature", out var msEl) && msEl.ValueKind == JsonValueKind.String ? msEl.GetString() : (item.TryGetProperty("methodSignature", out msEl) && msEl.ValueKind == JsonValueKind.String ? msEl.GetString() : null),
                            Address = addr,
                            Image = item.TryGetProperty("Image", out var imgEl) && imgEl.ValueKind == JsonValueKind.String ? imgEl.GetString() : (item.TryGetProperty("image", out imgEl) && imgEl.ValueKind == JsonValueKind.String ? imgEl.GetString() : null),
                            Assembly = item.TryGetProperty("Assembly", out var asmEl) && asmEl.ValueKind == JsonValueKind.String ? asmEl.GetString() : (item.TryGetProperty("assembly", out asmEl) && asmEl.ValueKind == JsonValueKind.String ? asmEl.GetString() : null)
                        });
                    }
                    artifacts.ScriptMethods = list;
                }
                catch (Exception ex) when (!(ex is InvalidDataException))
                {
                    throw new InvalidDataException($"Failed to parse {label} script.json: {ex.Message}", ex);
                }
            }

            // 4. Parse literals (optional, only stringliteral-indexed.json; if present must be valid)
            var indexedLiteralsPath = Path.Combine(dir, "stringliteral-indexed.json");
            if (File.Exists(indexedLiteralsPath))
            {
                try
                {
                    using var stream = File.OpenRead(indexedLiteralsPath);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException($"Failed to parse {label} stringliteral-indexed.json: root must be an array.");
                    }

                    var map = new Dictionary<uint, string>();
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (!item.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number)
                        {
                            throw new InvalidDataException($"Failed to parse {label} stringliteral-indexed.json: entry missing valid numeric 'index'.");
                        }
                        var val = item.TryGetProperty("value", out var valEl) ? (valEl.GetString() ?? "") : "";
                        map[idxEl.GetUInt32()] = val;
                    }
                    artifacts.Literals = map;
                }
                catch (Exception ex) when (!(ex is InvalidDataException))
                {
                    throw new InvalidDataException($"Failed to parse {label} stringliteral-indexed.json: {ex.Message}", ex);
                }
            }

            return artifacts;
        }
    }
}
