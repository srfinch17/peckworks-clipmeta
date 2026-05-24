namespace ClipMetaCore.Mp4;

/// <summary>Represents the header of a FullBox, which extends a standard box with a version byte and 24-bit flags field.</summary>
/// <param name="Box">The underlying box header containing size, type, and header-size information.</param>
/// <param name="Version">Version byte following the box header (typically 0 or 1).</param>
/// <param name="Flags">24-bit flags field following the version byte.</param>
public readonly record struct FullBoxHeader(
    BoxHeader Box,
    byte Version,
    uint Flags
);
