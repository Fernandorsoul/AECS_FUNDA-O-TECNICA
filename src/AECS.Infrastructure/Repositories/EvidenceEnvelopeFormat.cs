using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Infrastructure.Repositories;

internal static class EvidenceEnvelopeFormat
{
    public const string CurrentSchemaVersion = "aecs.execution-evidence/v1";

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };
}
