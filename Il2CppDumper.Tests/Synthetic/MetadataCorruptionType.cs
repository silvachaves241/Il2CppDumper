namespace Il2CppDumper.Tests.Synthetic
{
    /// <summary>
    /// Corruption and transformation variants covering parser fault injection, boundary cases, and string obfuscation (#38, #44).
    /// </summary>
    public enum MetadataCorruptionType
    {
        None = 0,
        TruncatedSection = 1,
        WrongMagic = 2,
        OutOfRangeVersion = 3,
        LiteralExtentPastSectionEnd = 4,
        ZeroedMetadataRecords = 5,
        ZeroedRegistrationRecords = ZeroedMetadataRecords,
        TruncatedHeader = 6,
        ObfuscatedStringLiteral = 7
    }
}
