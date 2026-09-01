using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

public sealed class LocalJsonProactiveAlertSink : IProactiveAlertSink
{
    private readonly string _inboxPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public LocalJsonProactiveAlertSink(string rootPath, string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        _inboxPath = Path.Combine(Path.GetFullPath(rootPath), "inbox");
        JsonProactiveAlertStore.EnsureOutsideRepository(
            _inboxPath,
            repositoryPath,
            "Local alert inbox");
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public string Name => "local-json";

    public ProactiveAlertSinkCapabilities Capabilities { get; } = new()
    {
        AcceptsCode = false,
        AcceptsSecrets = false
    };

    public async Task<ProactiveAlertDeliveryResult> DeliverAsync(
        ProactiveAlertDelivery delivery,
        AlertChannelAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(authorization);
        if (delivery.ContainsCode || delivery.ContainsSecrets)
        {
            return new ProactiveAlertDeliveryResult
            {
                Succeeded = false,
                FinishedAt = DateTime.UtcNow,
                Message = "Local JSON sink refuses code and secret content."
            };
        }
        if (delivery.SchemaVersion != ProactiveAlertSchema.DeliveryVersion ||
            delivery.AlertId == Guid.Empty || string.IsNullOrWhiteSpace(delivery.Recipient) ||
            string.IsNullOrWhiteSpace(delivery.EvidenceUri))
        {
            return new ProactiveAlertDeliveryResult
            {
                Succeeded = false,
                FinishedAt = DateTime.UtcNow,
                Message = "Alert delivery contract is invalid."
            };
        }

        Directory.CreateDirectory(_inboxPath);
        var finishedAt = DateTime.UtcNow;
        var fileName = $"{finishedAt:yyyyMMddHHmmssfffffff}-{delivery.AlertId:N}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(_inboxPath, fileName);
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(delivery, _jsonOptions),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return new ProactiveAlertDeliveryResult
        {
            Succeeded = true,
            FinishedAt = finishedAt,
            Message = "Alert persisted to the local JSON inbox.",
            Receipt = fileName
        };
    }
}
