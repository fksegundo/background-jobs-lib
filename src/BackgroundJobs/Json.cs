using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackgroundJobs;

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
