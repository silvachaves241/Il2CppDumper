#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumper.Diff
{
    internal static class DeclarationCoverage
    {
        internal static void EnsureComparable(ImageSelection? left, ImageSelection? right,
            IEnumerable<JsonDumpType> leftTypes, IEnumerable<JsonDumpType> rightTypes)
        {
            if (left == null && right == null)
                return;

            if (left == null || right == null)
            {
                var scopedSide = left == null ? "right" : "left";
                throw new InvalidDataException($"Incompatible image selection: only the {scopedSide} dump is scoped. " +
                    "Compare two full dumps or export the same image names on both sides.");
            }

            var leftNames = Validate(left, leftTypes, "left");
            var rightNames = Validate(right, rightTypes, "right");
            if (!leftNames.SetEquals(rightNames))
            {
                var leftOnly = leftNames.Except(rightNames, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);
                var rightOnly = rightNames.Except(leftNames, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);
                throw new InvalidDataException($"Incompatible image selection. Left-only images: [{string.Join(", ", leftOnly)}]. " +
                    $"Right-only images: [{string.Join(", ", rightOnly)}]. Export the same image names on both sides.");
            }
        }

        private static HashSet<string> Validate(ImageSelection selection, IEnumerable<JsonDumpType> types, string side)
        {
            if (selection.Resolved is not { Count: > 0 } || selection.Resolved.Count > selection.SourceImageCount)
                throw new InvalidDataException($"Invalid {side} image selection: expected a nonempty list of resolved images within the source image count.");

            var names = new HashSet<string>(StringComparer.Ordinal);
            var images = new Dictionary<int, SelectedImage>();
            foreach (var image in selection.Resolved)
            {
                if (image == null || string.IsNullOrWhiteSpace(image.ImageName) ||
                    image.ImageId < 0 || image.ImageId >= selection.SourceImageCount ||
                    (image.TypeStart < 0 && !(image.TypeStart == -1 && image.SourceTypeCount == 0)) ||
                    !images.TryAdd(image.ImageId, image))
                {
                    throw new InvalidDataException($"Invalid {side} image selection: missing or invalid image name, ID, or type range.");
                }
                if (!names.Add(image.ImageName))
                    throw new InvalidDataException($"Ambiguous {side} image selection: duplicate image name '{image.ImageName}'.");
            }

            var seenIndices = new HashSet<int>();
            var counts = new Dictionary<int, uint>();
            foreach (var type in types)
            {
                if (type.imageId is not int id || !images.TryGetValue(id, out var image) ||
                    !string.Equals(type.image, image.ImageName, StringComparison.Ordinal) ||
                    type.typeDefIndex is not int index || index < image.TypeStart ||
                    index >= (long)image.TypeStart + image.SourceTypeCount || !seenIndices.Add(index))
                {
                    throw new InvalidDataException($"Invalid {side} image coverage: type '{type.fullName}' has missing, duplicate, or out-of-scope coordinates.");
                }
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }

            foreach (var image in selection.Resolved)
            {
                uint count = counts.GetValueOrDefault(image.ImageId);
                if (count != image.SourceTypeCount)
                {
                    throw new InvalidDataException($"Incomplete {side} image coverage for '{image.ImageName}': " +
                        $"expected {image.SourceTypeCount} type declarations, found {count}.");
                }
            }

            return names;
        }
    }
}
