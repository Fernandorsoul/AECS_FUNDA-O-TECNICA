using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IProactiveAlertSink
{
    string Name { get; }
    ProactiveAlertSinkCapabilities Capabilities { get; }

    Task<ProactiveAlertDeliveryResult> DeliverAsync(
        ProactiveAlertDelivery delivery,
        AlertChannelAuthorization authorization,
        CancellationToken cancellationToken);
}
