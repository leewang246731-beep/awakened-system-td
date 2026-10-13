// T2: pure contract — K-3 system action schema, T-021, frozen (Wave 0; 07-decided-plan D-21).

namespace EraCore.Contracts.Actions;

/// <summary>
/// Closed-schema validator for system actions (K-3; the RL6 gate). One
/// schema source, two projections (07 D-14): the same hand-written draft
/// 2020-12 subset compiles into this internal validation schema and into
/// the L3 function-calling tools JSON (see K-6 <c>ILlmTransport</c>), so
/// game-side validation and LLM declarations can never drift apart.
/// </summary>
public interface ISchemaValidator
{
    /// <summary>
    /// Validates one action JSON document (it must carry the discriminating
    /// <c>action</c> verb). Pure: no state mutation, no I/O; the same input
    /// always yields the same verdict.
    /// </summary>
    SchemaValidationResult Validate(string actionJson);
}
