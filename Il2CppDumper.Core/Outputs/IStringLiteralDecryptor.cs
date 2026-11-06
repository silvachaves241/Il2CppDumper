#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Il2CppDumper
{
    /// <summary>
    /// Contract for pluggable string literal decryption transforms applied after
    /// literals are resolved from metadata.
    /// </summary>
    public interface IStringLiteralDecryptor
    {
        /// <summary>
        /// Gets the unique identifier for this decryptor recorded in output artifacts (e.g., "xor:2b").
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Decrypts a string literal from its index and raw metadata bytes.
        /// </summary>
        /// <param name="index">The zero-based string literal index in metadata.</param>
        /// <param name="rawBytes">The raw bytes extracted from the metadata string literal section.</param>
        /// <returns>The decrypted plaintext string.</returns>
        /// <exception cref="Exception">Thrown when decryption fails or ciphertext cannot be decoded.</exception>
        string Decrypt(uint index, byte[] rawBytes);
    }

    /// <summary>
    /// Built-in XOR string literal decryptor supporting single-byte and multi-byte keys.
    /// </summary>
    public sealed class XorStringLiteralDecryptor : IStringLiteralDecryptor
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly byte[] key;

        /// <inheritdoc />
        public string Id { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="XorStringLiteralDecryptor"/> using raw key bytes.
        /// </summary>
        /// <param name="keyBytes">Non-empty key byte array.</param>
        /// <param name="customId">Optional explicit identifier override.</param>
        public XorStringLiteralDecryptor(byte[] keyBytes, string? customId = null)
        {
            if (keyBytes == null || keyBytes.Length == 0)
                throw new ArgumentException("XOR key must not be null or empty.", nameof(keyBytes));

            key = (byte[])keyBytes.Clone();
            Id = customId ?? $"xor:{Convert.ToHexString(key).ToLowerInvariant()}";
        }

        /// <summary>
        /// Initializes a new instance of <see cref="XorStringLiteralDecryptor"/> from a hex string, numeric byte, or UTF-8 key string.
        /// </summary>
        /// <param name="keyString">Key representation (e.g. "2b", "0x2b", or multi-byte hex string).</param>
        public XorStringLiteralDecryptor(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
                throw new ArgumentException("XOR key string must not be empty.", nameof(keyString));

            var clean = keyString.Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);

            if (clean.Length % 2 == 0 && IsHex(clean))
            {
                key = Convert.FromHexString(clean);
            }
            else if (clean.Length == 1 && IsHex(clean))
            {
                key = Convert.FromHexString("0" + clean);
            }
            else if (byte.TryParse(clean, out var singleByte))
            {
                key = new[] { singleByte };
            }
            else
            {
                key = Encoding.UTF8.GetBytes(keyString);
            }

            if (key.Length == 0)
                throw new ArgumentException("XOR key resulted in an empty byte sequence.", nameof(keyString));

            Id = $"xor:{Convert.ToHexString(key).ToLowerInvariant()}";
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <inheritdoc />
        public string Decrypt(uint index, byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0)
                return string.Empty;

            var decrypted = new byte[rawBytes.Length];
            for (int i = 0; i < rawBytes.Length; i++)
            {
                decrypted[i] = (byte)(rawBytes[i] ^ key[i % key.Length]);
            }

            try
            {
                return StrictUtf8.GetString(decrypted);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"XOR decrypted bytes at index {index} failed UTF-8 validation: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Registry for resolving and registering string literal decryptors.
    /// </summary>
    public static class StringLiteralDecryptorRegistry
    {
        private static readonly ConcurrentDictionary<string, Func<string?, IStringLiteralDecryptor>> Factories =
            new(StringComparer.OrdinalIgnoreCase);

        static StringLiteralDecryptorRegistry()
        {
            Reset();
        }

        /// <summary>
        /// Resets the registry to built-in defaults.
        /// </summary>
        public static void Reset()
        {
            Factories.Clear();
            Register("xor", param =>
            {
                if (string.IsNullOrWhiteSpace(param))
                    throw new ArgumentException("XOR decryptor requires a key parameter (e.g. 'xor:2b' or config StringDecryptorKey).");
                return new XorStringLiteralDecryptor(param);
            });
        }

        /// <summary>
        /// Registers a factory for a named decryptor.
        /// </summary>
        /// <param name="name">Name or scheme prefix.</param>
        /// <param name="factory">Factory delegate accepting an optional key parameter.</param>
        public static void Register(string name, Func<string?, IStringLiteralDecryptor> factory)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            Factories[name] = factory;
        }

        /// <summary>
        /// Registers a singleton decryptor instance under its Id.
        /// </summary>
        /// <param name="decryptor">The decryptor instance to register.</param>
        public static void Register(IStringLiteralDecryptor decryptor)
        {
            if (decryptor == null)
                throw new ArgumentNullException(nameof(decryptor));

            Factories[decryptor.Id] = _ => decryptor;
        }

        /// <summary>
        /// Resolves a decryptor from a specification string and optional fallback key parameter.
        /// Returns null when spec is null, empty, or "none".
        /// </summary>
        /// <param name="decryptorSpec">Specification string (e.g. "none", "xor", "xor:2b", or custom name).</param>
        /// <param name="fallbackKey">Optional key when not embedded in spec.</param>
        /// <returns>Resolved <see cref="IStringLiteralDecryptor"/> or null.</returns>
        public static IStringLiteralDecryptor? Resolve(string? decryptorSpec, string? fallbackKey = null)
        {
            if (string.IsNullOrWhiteSpace(decryptorSpec) || decryptorSpec.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var trimmed = decryptorSpec.Trim();
            var colonIdx = trimmed.IndexOf(':');
            string name;
            string? param = fallbackKey;

            if (colonIdx >= 0)
            {
                name = trimmed.Substring(0, colonIdx);
                param ??= trimmed.Substring(colonIdx + 1);
            }
            else
            {
                name = trimmed;
            }

            if (Factories.TryGetValue(name, out var factory))
            {
                return factory(param);
            }

            if (Factories.TryGetValue(trimmed, out var fullFactory))
            {
                return fullFactory(param);
            }

            throw new KeyNotFoundException($"No string literal decryptor registered with name '{name}' (spec: '{decryptorSpec}').");
        }
    }
}
