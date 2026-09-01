using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Models;

namespace AECS.Application.ProactiveAlerts;

public static class ProactiveAlertPolicyJson
{
    private const int MaximumPolicyBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static ProactiveAlertPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
            throw new FileNotFoundException("Alert policy file was not found.", fullPath);
        if (file.Length > MaximumPolicyBytes)
            throw new InvalidDataException("Alert policy exceeds the 256 KiB limit.");
        var json = File.ReadAllText(fullPath);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectDuplicateProperties(document.RootElement);
        var policy = document.RootElement.Deserialize<ProactiveAlertPolicy>(Options) ??
            throw new InvalidDataException("Alert policy file is empty.");
        ProactiveAlertPolicyValidator.Validate(policy);
        return policy;
    }

    public static string ToJson(object value) =>
        JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Alert policy contains duplicate property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}
