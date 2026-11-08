#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumper.Diff
{
    internal readonly record struct TypeIdentityKey(string? Image, string? Assembly, string FullName)
    {
        public bool HasAttribution => !string.IsNullOrEmpty(Image) || !string.IsNullOrEmpty(Assembly);

        public string? PreferredScope => !string.IsNullOrEmpty(Image) ? Image : Assembly;

        public string FormatDisplayKey() => JsonDumpType.FormatKey(Image, Assembly, FullName);
    }

    public class ReconciledType
    {
        public string Key { get; }
        public string? Image { get; }
        public string? Assembly { get; }
        public string FullName { get; }
        public JsonDumpType? Left { get; }
        public JsonDumpType? Right { get; }

        public bool IsMatched => Left != null && Right != null;
        public bool IsAdded => Left == null && Right != null;
        public bool IsRemoved => Left != null && Right == null;

        public ReconciledType(string key, string? image, string? assembly, string fullName, JsonDumpType? left, JsonDumpType? right)
        {
            Key = key;
            Image = image;
            Assembly = assembly;
            FullName = fullName;
            Left = left;
            Right = right;
        }
    }

    public static class TypeReconciler
    {
        public static List<ReconciledType> Reconcile(JsonDumpModel? leftModel, JsonDumpModel? rightModel)
        {
            var leftItems = ExtractTypes(leftModel, "left");
            var rightItems = ExtractTypes(rightModel, "right");

            ValidateSingleSide(leftItems, "left");
            ValidateSingleSide(rightItems, "right");

            var leftByFullName = leftItems
                .GroupBy(x => x.Key.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var rightByFullName = rightItems
                .GroupBy(x => x.Key.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var allFullNames = leftByFullName.Keys
                .Union(rightByFullName.Keys, StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var reconciled = new List<ReconciledType>();
            var ambiguousCollisions = new HashSet<string>(StringComparer.Ordinal);

            foreach (var fullName in allFullNames)
            {
                leftByFullName.TryGetValue(fullName, out var leftGroup);
                rightByFullName.TryGetValue(fullName, out var rightGroup);

                // Case 1: Present only on left (Removed)
                if (rightGroup == null || rightGroup.Count == 0)
                {
                    foreach (var (key, type) in leftGroup!)
                    {
                        var displayKey = JsonDumpType.FormatKey(type.image, type.assembly, fullName);
                        reconciled.Add(new ReconciledType(displayKey, type.image, type.assembly, fullName, type, null));
                    }
                    continue;
                }

                // Case 2: Present only on right (Added)
                if (leftGroup == null || leftGroup.Count == 0)
                {
                    foreach (var (key, type) in rightGroup)
                    {
                        var displayKey = JsonDumpType.FormatKey(type.image, type.assembly, fullName);
                        reconciled.Add(new ReconciledType(displayKey, type.image, type.assembly, fullName, null, type));
                    }
                    continue;
                }

                // Case 3: Present on both sides — reconcile candidates sharing this fullName
                var leftRemaining = new List<(TypeIdentityKey Key, JsonDumpType Type)>(leftGroup);
                var rightRemaining = new List<(TypeIdentityKey Key, JsonDumpType Type)>(rightGroup);

                // Pass 1: Exact image match (when both sides specify image)
                for (int i = leftRemaining.Count - 1; i >= 0; i--)
                {
                    var left = leftRemaining[i];
                    if (string.IsNullOrEmpty(left.Key.Image)) continue;

                    for (int j = rightRemaining.Count - 1; j >= 0; j--)
                    {
                        var right = rightRemaining[j];
                        if (string.IsNullOrEmpty(right.Key.Image)) continue;

                        if (string.Equals(left.Key.Image, right.Key.Image, StringComparison.Ordinal))
                        {
                            var image = right.Type.image ?? left.Type.image;
                            var assembly = right.Type.assembly ?? left.Type.assembly;
                            var displayKey = JsonDumpType.FormatKey(image, assembly, fullName);
                            reconciled.Add(new ReconciledType(displayKey, image, assembly, fullName, left.Type, right.Type));

                            leftRemaining.RemoveAt(i);
                            rightRemaining.RemoveAt(j);
                            break;
                        }
                    }
                }

                // Pass 2: Exact assembly match (when neither specifies image, but both specify assembly)
                for (int i = leftRemaining.Count - 1; i >= 0; i--)
                {
                    var left = leftRemaining[i];
                    if (!string.IsNullOrEmpty(left.Key.Image) || string.IsNullOrEmpty(left.Key.Assembly)) continue;

                    for (int j = rightRemaining.Count - 1; j >= 0; j--)
                    {
                        var right = rightRemaining[j];
                        if (!string.IsNullOrEmpty(right.Key.Image) || string.IsNullOrEmpty(right.Key.Assembly)) continue;

                        if (string.Equals(left.Key.Assembly, right.Key.Assembly, StringComparison.Ordinal))
                        {
                            var displayKey = JsonDumpType.FormatKey(null, left.Key.Assembly, fullName);
                            reconciled.Add(new ReconciledType(displayKey, null, left.Key.Assembly, fullName, left.Type, right.Type));

                            leftRemaining.RemoveAt(i);
                            rightRemaining.RemoveAt(j);
                            break;
                        }
                    }
                }

                // Pass 3: Full legacy match (neither side specifies image nor assembly)
                for (int i = leftRemaining.Count - 1; i >= 0; i--)
                {
                    var left = leftRemaining[i];
                    if (left.Key.HasAttribution) continue;

                    for (int j = rightRemaining.Count - 1; j >= 0; j--)
                    {
                        var right = rightRemaining[j];
                        if (right.Key.HasAttribution) continue;

                        reconciled.Add(new ReconciledType(fullName, null, null, fullName, left.Type, right.Type));

                        leftRemaining.RemoveAt(i);
                        rightRemaining.RemoveAt(j);
                        break;
                    }
                }

                // Pass 4: Partial assembly fallback (one specifies image and assembly, other specifies only assembly)
                // Group remaining unmatched by assembly: only match if strictly unique (1 left, 1 right in that assembly)
                var remainingAssemblies = leftRemaining.Select(x => x.Key.Assembly)
                    .Concat(rightRemaining.Select(x => x.Key.Assembly))
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var asm in remainingAssemblies)
                {
                    var leftAsmMatches = leftRemaining.Where(x => string.Equals(x.Key.Assembly, asm, StringComparison.Ordinal)).ToList();
                    var rightAsmMatches = rightRemaining.Where(x => string.Equals(x.Key.Assembly, asm, StringComparison.Ordinal)).ToList();

                    if (leftAsmMatches.Count > 0 && rightAsmMatches.Count > 0)
                    {
                        if (leftAsmMatches.Count == 1 && rightAsmMatches.Count == 1)
                        {
                            var left = leftAsmMatches[0];
                            var right = rightAsmMatches[0];

                            var image = right.Type.image ?? left.Type.image;
                            var assembly = right.Type.assembly ?? left.Type.assembly;
                            var displayKey = JsonDumpType.FormatKey(image, assembly, fullName);
                            reconciled.Add(new ReconciledType(displayKey, image, assembly, fullName, left.Type, right.Type));

                            leftRemaining.Remove(left);
                            rightRemaining.Remove(right);
                        }
                        else
                        {
                            ambiguousCollisions.Add(fullName);
                        }
                    }
                }

                // Pass 5: Unattributed mixed fallback (legacy dump vs modern dump with attribution)
                // Only allow fallback if the type is strictly unique across both sets (1 left, 1 right in total for this fullName)
                if (leftRemaining.Count > 0 && rightRemaining.Count > 0)
                {
                    bool leftHasUnattributed = leftRemaining.Any(x => !x.Key.HasAttribution);
                    bool rightHasUnattributed = rightRemaining.Any(x => !x.Key.HasAttribution);

                    if (leftHasUnattributed || rightHasUnattributed)
                    {
                        if (leftGroup.Count == 1 && rightGroup.Count == 1 && leftRemaining.Count == 1 && rightRemaining.Count == 1)
                        {
                            var left = leftRemaining[0];
                            var right = rightRemaining[0];

                            var image = right.Type.image ?? left.Type.image;
                            var assembly = right.Type.assembly ?? left.Type.assembly;
                            var displayKey = JsonDumpType.FormatKey(image, assembly, fullName);
                            reconciled.Add(new ReconciledType(displayKey, image, assembly, fullName, left.Type, right.Type));

                            leftRemaining.Clear();
                            rightRemaining.Clear();
                        }
                        else
                        {
                            ambiguousCollisions.Add(fullName);
                        }
                    }
                }

                // Pass 6: Any remaining unmatched items are added or removed
                foreach (var left in leftRemaining)
                {
                    var displayKey = JsonDumpType.FormatKey(left.Type.image, left.Type.assembly, fullName);
                    reconciled.Add(new ReconciledType(displayKey, left.Type.image, left.Type.assembly, fullName, left.Type, null));
                }

                foreach (var right in rightRemaining)
                {
                    var displayKey = JsonDumpType.FormatKey(right.Type.image, right.Type.assembly, fullName);
                    reconciled.Add(new ReconciledType(displayKey, right.Type.image, right.Type.assembly, fullName, null, right.Type));
                }
            }

            if (ambiguousCollisions.Count > 0)
            {
                var sortedAmbiguous = ambiguousCollisions.OrderBy(x => x, StringComparer.Ordinal);
                throw new InvalidDataException($"Ambiguous type collision between dumps: duplicate type(s) with partial or missing attribution cannot be uniquely matched: {string.Join(", ", sortedAmbiguous)}");
            }

            return reconciled;
        }

        private static List<(TypeIdentityKey Key, JsonDumpType Type)> ExtractTypes(JsonDumpModel? model, string label)
        {
            if (model == null || model.types == null)
            {
                throw new InvalidDataException($"Invalid {label} dump.json: missing 'types' array property.");
            }

            var list = new List<(TypeIdentityKey Key, JsonDumpType Type)>();

            void Collect(JsonDumpType t, string? parentImage, string? parentAssembly)
            {
                if (string.IsNullOrEmpty(t.image)) t.image = parentImage;
                if (string.IsNullOrEmpty(t.assembly)) t.assembly = parentAssembly;

                if (!string.IsNullOrEmpty(t.fullName))
                {
                    var key = new TypeIdentityKey(t.image, t.assembly, t.fullName);
                    list.Add((key, t));
                }

                if (t.nested != null)
                {
                    foreach (var child in t.nested)
                    {
                        Collect(child, t.image, t.assembly);
                    }
                }
            }

            foreach (var t in model.types)
            {
                Collect(t, null, null);
            }

            return list;
        }

        private static void ValidateSingleSide(List<(TypeIdentityKey Key, JsonDumpType Type)> items, string label)
        {
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            var byFullName = items.GroupBy(x => x.Key.FullName, StringComparer.Ordinal);

            foreach (var group in byFullName)
            {
                var list = group.ToList();
                if (list.Count <= 1) continue;

                if (list.Any(x => !x.Key.HasAttribution))
                {
                    duplicates.Add(group.Key);
                    continue;
                }

                var seenScopes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in list)
                {
                    var scope = item.Key.PreferredScope!;
                    if (!seenScopes.Add(scope))
                    {
                        duplicates.Add(item.Key.FormatDisplayKey());
                    }
                }
            }

            if (duplicates.Count > 0)
            {
                var sortedDups = duplicates.OrderBy(x => x, StringComparer.Ordinal);
                throw new InvalidDataException($"Ambiguous type collision in {label} dump.json: duplicate type(s) lack distinct image/assembly identity to resolve collisions: {string.Join(", ", sortedDups)}");
            }
        }
    }
}
