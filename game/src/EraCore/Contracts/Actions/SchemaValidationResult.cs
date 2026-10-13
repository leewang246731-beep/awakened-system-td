// T2: pure contract — K-3 system action schema, T-021, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Actions;

/// <summary>
/// One schema-validation error (K-3). Built on JsonSchema.Net (07 D-14):
/// the JSON pointer of the offending property plus the source line from
/// parse time, so invalid action or content JSON reports line-level errors.
/// </summary>
/// <param name="Pointer">Schema-relative pointer of the violated constraint (e.g. <c>/cells/3</c>).</param>
/// <param name="Line">1-based line in the input JSON; 0 when the position is unknown.</param>
/// <param name="Message">What failed — constraint summary only, never secrets.</param>
public sealed record SchemaValidationError(
    string Pointer,
    int Line,
    string Message);

/// <summary>Outcome of <see cref="ISchemaValidator.Validate"/> (K-3).</summary>
/// <param name="IsValid">True when the document satisfies the closed schema of its action verb.</param>
/// <param name="Errors">Every violation found, in document order; empty when valid.</param>
public sealed record SchemaValidationResult(
    bool IsValid,
    IReadOnlyList<SchemaValidationError> Errors);
