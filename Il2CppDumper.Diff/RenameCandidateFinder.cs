using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Il2CppDumper.Diff;

internal static class RenameCandidateFinder
{
    private const string PrivateImplementationGuid = "private-implementation-guid";
    private const string StateMachineOrdinal = "state-machine-ordinal";
    private const string MatchingDeclarations = "matching-field-method-shape";
    private static readonly Regex PrivateDetails = new(
        @"(?:\A|(?<=\.))<PrivateImplementationDetails>\{[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}(?=\.|\z)",
        RegexOptions.CultureInvariant);
    private static readonly Regex StateMachineSuffix = new(@"(\.<[^<>]+>d__)[0-9]+\z", RegexOptions.CultureInvariant);

    internal static RenameCandidates? Find(List<ReconciledType> reconciled)
    {
        var report = new RenameCandidates();
        var removed = reconciled.Where(item => item.IsRemoved && !string.IsNullOrEmpty(item.Left!.image))
            .Select(item => item.Left!).ToArray();
        var added = reconciled.Where(item => item.IsAdded && !string.IsNullOrEmpty(item.Right!.image))
            .Select(item => item.Right!).ToArray();
        if (removed.Length > 0 && added.Length > 0)
            FindTypes(reconciled, removed, added, report.types);

        foreach (var item in reconciled.Where(item => item.IsMatched).OrderBy(item => item.Key, StringComparer.Ordinal))
            FindFields(item, report.fields);

        return report.types.Count == 0 && report.fields.Count == 0 ? null : report;
    }

    private static void FindTypes(List<ReconciledType> reconciled, JsonDumpType[] removed, JsonDumpType[] added,
        List<TypeRenameCandidate> results)
    {
        var leftTokens = IndexTokens(reconciled.Select(item => item.Left).OfType<JsonDumpType>());
        var rightTokens = IndexTokens(reconciled.Select(item => item.Right).OfType<JsonDumpType>());
        var leftGroups = GroupByAnchor(removed);
        var rightGroups = GroupByAnchor(added);
        var covered = new HashSet<JsonDumpType>();

        foreach (var group in leftGroups)
        {
            var right = rightGroups[group.Key].ToArray();
            if (right.Length == 0)
                continue;

            var left = group.ToArray();
            covered.UnionWith(left);
            covered.UnionWith(right);
            var evidence = new List<string> { "same-image", group.Key.Rule };
            if (left.Length != 1 || right.Length != 1)
            {
                results.Add(CreateGroup(left, right, evidence.ToArray(), new[] { "multiple-matches" }));
                continue;
            }

            var before = left[0];
            var after = right[0];
            var ambiguities = new List<string>();
            if (group.Key.Rule == StateMachineOrdinal)
            {
                var shape = DeclarationShape(before, includeOffsets: false);
                if (shape != null && shape == DeclarationShape(after, includeOffsets: false))
                    evidence.Add(MatchingDeclarations);
                else
                    ambiguities.Add("insufficient-declaration-evidence");
            }

            if (HasTypeToken(before) && before.typeToken == after.typeToken)
                evidence.Add("same-type-token");
            if (before.typeDefIndex is >= 0 && before.typeDefIndex == after.typeDefIndex)
                evidence.Add("same-type-def-index");
            if (TokenConflicts(before, after, leftTokens, rightTokens) ||
                TokenConflicts(after, before, rightTokens, leftTokens))
                ambiguities.Add("type-token-conflict");

            var candidate = CreateGroup(left, right, evidence.ToArray(), ambiguities.ToArray());
            if (ambiguities.Count == 0)
            {
                var pair = new ReconciledType(after.GetIdentityKey(), after.image, after.assembly, after.fullName, before, after);
                var layout = FieldOffsetDiff.CompareType(pair, out var matchingOffsets);
                candidate = candidate with
                {
                    declarationChanges = CompareDeclarations(before, after),
                    layoutChanges = layout,
                    matchingInstanceOffsets = matchingOffsets
                };
            }
            results.Add(candidate);
        }

        // A token shared by the remaining unmatched names warrants inspection without establishing a pair.
        var unmatchedLeftTokens = IndexTokens(removed.Where(type => !covered.Contains(type)));
        var unmatchedRightTokens = IndexTokens(added.Where(type => !covered.Contains(type)));
        foreach (var group in unmatchedLeftTokens)
        {
            var right = unmatchedRightTokens[group.Key].ToArray();
            if (right.Length > 0)
                results.Add(CreateGroup(group.ToArray(), right, new[] { "same-image", "same-type-token" },
                    new[] { "token-without-declaration-match" }));
        }
        results.Sort((left, right) => StringComparer.Ordinal.Compare(left.left[0].type, right.left[0].type));
    }

    private static ILookup<(string Image, string Rule, string Key), JsonDumpType> GroupByAnchor(JsonDumpType[] types) =>
        types.Select(type => (Type: type, Anchor: GetAnchor(type)))
            .Where(item => item.Anchor.HasValue)
            .ToLookup(item => (item.Type.image!, item.Anchor!.Value.Rule, item.Anchor.Value.Key), item => item.Type);

    private static (string Rule, string Key)? GetAnchor(JsonDumpType type)
    {
        var normalized = PrivateDetails.Replace(type.fullName, "<PrivateImplementationDetails>{GUID}");
        if (!string.Equals(normalized, type.fullName, StringComparison.Ordinal))
            return (PrivateImplementationGuid, normalized);

        var suffix = StateMachineSuffix.Match(type.fullName);
        if (suffix.Success && suffix.Index > 0)
            return (StateMachineOrdinal, type.fullName[..(suffix.Index + suffix.Groups[1].Length)]);

        var shape = DeclarationShape(type, includeOffsets: true);
        return shape == null ? null : (MatchingDeclarations, shape);
    }

    private static string? DeclarationShape(JsonDumpType type, bool includeOffsets)
    {
        if (type.fields == null || type.methods == null ||
            !type.fields.Any(field => !field.isStatic && !string.IsNullOrEmpty(field.name) &&
                !string.IsNullOrEmpty(field.type) && FieldOffsetDiff.TryParseHexOffset(field.offset, out _)) ||
            !type.methods.Any(method => !string.IsNullOrEmpty(method.name) && !string.IsNullOrEmpty(method.returnType)))
            return null;

        return JsonSerializer.Serialize(new
        {
            type.bases,
            fields = type.fields.Select(field => new
            {
                field.name, field.type, field.isStatic,
                offset = includeOffsets && FieldOffsetDiff.TryParseHexOffset(field.offset, out var offset) ? offset : (ulong?)null
            }),
            methods = type.methods.Select(method => new
            {
                method.name, method.returnType, method.isStatic,
                parameters = method.parameters?.Select(parameter => parameter.type)
            })
        });
    }

    private static bool HasTypeToken(JsonDumpType type) => type.typeToken is > 0x02000000 and <= 0x02FFFFFF;

    private static ILookup<(string Image, uint Token), JsonDumpType> IndexTokens(IEnumerable<JsonDumpType> types) =>
        types.Where(type => !string.IsNullOrEmpty(type.image) && HasTypeToken(type))
            .ToLookup(type => (type.image!, type.typeToken!.Value));

    private static bool TokenConflicts(JsonDumpType type, JsonDumpType counterpart,
        ILookup<(string Image, uint Token), JsonDumpType> ownIndex,
        ILookup<(string Image, uint Token), JsonDumpType> otherIndex)
    {
        if (!HasTypeToken(type))
            return false;
        var key = (type.image!, type.typeToken!.Value);
        return ownIndex[key].Any(other => !ReferenceEquals(type, other)) ||
            otherIndex[key].Any(other => !ReferenceEquals(counterpart, other));
    }

    private static TypeRenameCandidate CreateGroup(JsonDumpType[] left, JsonDumpType[] right, string[] evidence,
        string[] ambiguities) => new(Endpoints(left), Endpoints(right), evidence, ambiguities);

    private static TypeRenameEndpoint[] Endpoints(JsonDumpType[] types) =>
        types.Select(type => new TypeRenameEndpoint(type.GetIdentityKey(), type.assembly, type.typeDefIndex, type.typeToken))
            .OrderBy(endpoint => endpoint.type, StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, RenameDeclarationChange> CompareDeclarations(JsonDumpType before, JsonDumpType after)
    {
        var changes = new Dictionary<string, RenameDeclarationChange>(StringComparer.Ordinal);
        Add("assembly", before.assembly, after.assembly);
        Add("bases", before.bases, after.bases);
        Add("attributes", before.attributes, after.attributes);
        Add("fields", before.fields, after.fields);
        Add("properties", before.properties, after.properties);
        Add("methods", Methods(before), Methods(after));
        Add("enums", before.enums, after.enums);
        if (!NestedNames(before, relative: true).SequenceEqual(NestedNames(after, relative: true), StringComparer.Ordinal))
            Add("nested", NestedNames(before, relative: false), NestedNames(after, relative: false));
        Add("size", before.size, after.size);
        return changes;

        void Add<T>(string section, T left, T right)
        {
            var lhs = JsonSerializer.SerializeToElement(left);
            var rhs = JsonSerializer.SerializeToElement(right);
            if (lhs.GetRawText() != rhs.GetRawText())
                changes.Add(section, new RenameDeclarationChange(lhs, rhs));
        }

        // Index coordinates and reverse dispatch are build-specific metadata, beyond this comparison of declarations.
        static object? Methods(JsonDumpType type) =>
            type.methods?.Select(method => new
            {
                method.name, method.returnType, method.parameters, method.isStatic, method.attributes
            });
    }

    private static IEnumerable<string> NestedNames(JsonDumpType owner, bool relative)
    {
        var prefix = owner.fullName + ".";
        return owner.nested?.Select(type => relative && type.fullName.StartsWith(prefix, StringComparison.Ordinal)
            ? type.fullName[prefix.Length..]
            : type.fullName) ?? Enumerable.Empty<string>();
    }

    private static void FindFields(ReconciledType item, List<FieldRenameCandidate> results)
    {
        var before = item.Left!.fields;
        var after = item.Right!.fields;
        if (before == null || after == null || before.Count == 0 || after.Count == 0)
            return;

        var beforeNames = before.Select(field => field.name).ToHashSet(StringComparer.Ordinal);
        var afterNames = after.Select(field => field.name).ToHashSet(StringComparer.Ordinal);
        if (beforeNames.SetEquals(afterNames))
            return;

        var leftOffsets = InstanceOffsets(before);
        var rightOffsets = InstanceOffsets(after);
        foreach (var group in leftOffsets.OrderBy(group => group.Key))
        {
            var left = group.Where(field => !afterNames.Contains(field.name)).ToArray();
            var rightGroup = rightOffsets[group.Key];
            var right = rightGroup.Where(field => !beforeNames.Contains(field.name)).ToArray();
            if (left.Length == 0 || right.Length == 0)
                continue;

            var ambiguities = new List<string>(2);
            if (group.Count() > 1 || rightGroup.Count() > 1)
                ambiguities.Add("shared-instance-offset");
            var fieldTypes = left.Concat(right).Select(field => field.type).Distinct(StringComparer.Ordinal).ToArray();
            if (fieldTypes.Length != 1 || string.IsNullOrEmpty(fieldTypes[0]))
                ambiguities.Add("different-declared-field-types");
            results.Add(new FieldRenameCandidate(item.Key, $"0x{group.Key:X}", left, right, ambiguities.ToArray()));
        }
    }

    private static ILookup<ulong, JsonDumpField> InstanceOffsets(IEnumerable<JsonDumpField> fields)
    {
        var entries = new List<(ulong Offset, JsonDumpField Field)>();
        foreach (var field in fields)
        {
            if (!field.isStatic && FieldOffsetDiff.TryParseHexOffset(field.offset, out var offset))
                entries.Add((offset, field));
        }
        return entries.ToLookup(entry => entry.Offset, entry => entry.Field);
    }
}
