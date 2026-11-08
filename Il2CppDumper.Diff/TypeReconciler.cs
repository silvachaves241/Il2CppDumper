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

        public bool IsScopeCompatible(TypeIdentityKey other)
        {
            if (!string.IsNullOrEmpty(Image) && !string.IsNullOrEmpty(other.Image))
            {
                return string.Equals(Image, other.Image, StringComparison.Ordinal);
            }

            if (!string.IsNullOrEmpty(Assembly) && !string.IsNullOrEmpty(other.Assembly))
            {
                return string.Equals(Assembly, other.Assembly, StringComparison.Ordinal);
            }

            return false;
        }
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

            var matchedLeft = new bool[leftItems.Count];
            var matchedRight = new bool[rightItems.Count];
            var reconciled = new List<ReconciledType>();

            // 1. Qualified matches: both sides have explicit attribution
            for (int i = 0; i < leftItems.Count; i++)
            {
                var (leftKey, leftType) = leftItems[i];
                if (!leftKey.HasAttribution) continue;

                for (int j = 0; j < rightItems.Count; j++)
                {
                    if (matchedRight[j]) continue;
                    var (rightKey, rightType) = rightItems[j];
                    if (!rightKey.HasAttribution) continue;

                    if (string.Equals(leftKey.FullName, rightKey.FullName, StringComparison.Ordinal) &&
                        leftKey.IsScopeCompatible(rightKey))
                    {
                        matchedLeft[i] = true;
                        matchedRight[j] = true;
                        var image = rightType.image ?? leftType.image;
                        var assembly = rightType.assembly ?? leftType.assembly;
                        var key = JsonDumpType.FormatKey(image, assembly, rightKey.FullName);
                        reconciled.Add(new ReconciledType(key, image, assembly, rightKey.FullName, leftType, rightType));
                        break;
                    }
                }
            }

            // 2. Legacy matches: neither side has explicit attribution
            for (int i = 0; i < leftItems.Count; i++)
            {
                if (matchedLeft[i]) continue;
                var (leftKey, leftType) = leftItems[i];
                if (leftKey.HasAttribution) continue;

                for (int j = 0; j < rightItems.Count; j++)
                {
                    if (matchedRight[j]) continue;
                    var (rightKey, rightType) = rightItems[j];
                    if (rightKey.HasAttribution) continue;

                    if (string.Equals(leftKey.FullName, rightKey.FullName, StringComparison.Ordinal))
                    {
                        matchedLeft[i] = true;
                        matchedRight[j] = true;
                        reconciled.Add(new ReconciledType(leftKey.FullName, null, null, leftKey.FullName, leftType, rightType));
                        break;
                    }
                }
            }

            // 3. Mixed fallback: one side lacks attribution, other side has attribution.
            // Allowed ONLY IF the match is strictly unique across both sets for that fullName.
            var leftCountsByName = leftItems.GroupBy(x => x.Key.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var rightCountsByName = rightItems.GroupBy(x => x.Key.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var ambiguousCollisions = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < leftItems.Count; i++)
            {
                if (matchedLeft[i]) continue;
                var (leftKey, leftType) = leftItems[i];

                for (int j = 0; j < rightItems.Count; j++)
                {
                    if (matchedRight[j]) continue;
                    var (rightKey, rightType) = rightItems[j];

                    if (!string.Equals(leftKey.FullName, rightKey.FullName, StringComparison.Ordinal))
                        continue;

                    // If both have attribution, DO NOT fallback across explicitly different scopes
                    if (leftKey.HasAttribution && rightKey.HasAttribution)
                        continue;

                    int totalLeft = leftCountsByName[leftKey.FullName];
                    int totalRight = rightCountsByName[rightKey.FullName];

                    if (totalLeft == 1 && totalRight == 1)
                    {
                        matchedLeft[i] = true;
                        matchedRight[j] = true;
                        var image = rightType.image ?? leftType.image;
                        var assembly = rightType.assembly ?? leftType.assembly;
                        var key = JsonDumpType.FormatKey(image, assembly, leftKey.FullName);
                        reconciled.Add(new ReconciledType(key, image, assembly, leftKey.FullName, leftType, rightType));
                        break;
                    }
                    else
                    {
                        ambiguousCollisions.Add(leftKey.FullName);
                    }
                }
            }

            if (ambiguousCollisions.Count > 0)
            {
                var sortedAmbiguous = ambiguousCollisions.OrderBy(x => x, StringComparer.Ordinal);
                throw new InvalidDataException($"Ambiguous type collision between dumps: duplicate type(s) with partial or missing attribution cannot be uniquely matched: {string.Join(", ", sortedAmbiguous)}");
            }

            // 4. Unmatched remaining items (Removed or Added)
            for (int i = 0; i < leftItems.Count; i++)
            {
                if (!matchedLeft[i])
                {
                    var (leftKey, leftType) = leftItems[i];
                    var key = JsonDumpType.FormatKey(leftType.image, leftType.assembly, leftKey.FullName);
                    reconciled.Add(new ReconciledType(key, leftType.image, leftType.assembly, leftKey.FullName, leftType, null));
                }
            }

            for (int j = 0; j < rightItems.Count; j++)
            {
                if (!matchedRight[j])
                {
                    var (rightKey, rightType) = rightItems[j];
                    var key = JsonDumpType.FormatKey(rightType.image, rightType.assembly, rightKey.FullName);
                    reconciled.Add(new ReconciledType(key, rightType.image, rightType.assembly, rightKey.FullName, null, rightType));
                }
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
