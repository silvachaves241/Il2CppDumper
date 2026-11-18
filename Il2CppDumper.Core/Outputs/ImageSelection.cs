#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Il2CppDumper
{
    /// <summary>Resolved declaration scope in the input metadata's original coordinate space.</summary>
    public sealed record ImageSelection(
        [property: JsonPropertyName("requested")] IReadOnlyList<string> Requested,
        [property: JsonPropertyName("sourceImageCount")] int SourceImageCount,
        [property: JsonPropertyName("resolved")] IReadOnlyList<SelectedImage> Resolved)
    {
        internal static ImageSelection? Resolve(Metadata metadata, string[]? selectors)
        {
            if (selectors is not { Length: > 0 })
                return null;

            var images = metadata.imageDefs.Select((image, id) => new SelectedImage(id,
                metadata.GetStringFromIndex(image.nameIndex), image.typeStart, image.typeCount)).ToArray();
            var byName = images.ToLookup(image => image.ImageName, StringComparer.OrdinalIgnoreCase);
            var selectedIds = new HashSet<int>();
            foreach (var selector in selectors)
            {
                if (string.IsNullOrWhiteSpace(selector))
                    throw new ArgumentException("Image selectors must be exact names or id:N.");

                if (selector.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(selector.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                        || (uint)id >= (uint)images.Length)
                        throw new ArgumentException($"Unknown or invalid image ID '{selector}'.");
                    selectedIds.Add(id);
                    continue;
                }

                var matches = byName[selector].ToArray();
                if (matches.Length == 0)
                    throw new ArgumentException($"Unknown image name '{selector}'. Use the full metadata image name.");
                if (matches.Length > 1)
                    throw new ArgumentException($"Ambiguous image name '{selector}'. Choose {string.Join(", ", matches.Select(image => $"id:{image.ImageId}"))}.");
                selectedIds.Add(matches[0].ImageId);
            }

            return new ImageSelection(Array.AsReadOnly((string[])selectors.Clone()), images.Length,
                Array.AsReadOnly(images.Where(image => selectedIds.Contains(image.ImageId)).ToArray()));
        }

        internal static IEnumerable<int> ImageIds(Metadata metadata, ImageSelection? selection) =>
            selection?.Resolved.Select(image => image.ImageId) ?? Enumerable.Range(0, metadata.imageDefs.Length);
    }

    public sealed record SelectedImage(
        [property: JsonPropertyName("imageId")] int ImageId,
        [property: JsonPropertyName("imageName")] string ImageName,
        [property: JsonPropertyName("typeStart")] int TypeStart,
        [property: JsonPropertyName("sourceTypeCount")] uint SourceTypeCount);
}
