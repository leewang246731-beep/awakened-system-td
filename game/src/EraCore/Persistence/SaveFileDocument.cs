namespace EraCore.Persistence;

/// <summary>
/// Envelope of every physical file the Web JSON save backend writes
/// (T-100 / 07 D-32): the format-version authority plus the domain payload
/// as a JSON string. Two layers on purpose — the exact layering of the
/// SQLite backend's <c>schema_version</c> authority row and
/// <c>payload_json</c> column — so damage semantics match: a decodable
/// envelope with a damaged payload string surfaces as
/// <see cref="SaveCorruptException"/> carrying the STORED version (the
/// authority survived), while an undecodable envelope is "no version
/// authority" (stored version 0). <see cref="FormatVersion"/> is nullable
/// to keep "field absent" (a store this build may overwrite, mirroring a
/// SQLite database without the <c>schema_version</c> table) distinct from
/// an explicit value such as 0 (an authority that the migration chain must
/// resolve).
/// </summary>
public sealed record SaveFileDocument(int? FormatVersion, string? PayloadJson);
