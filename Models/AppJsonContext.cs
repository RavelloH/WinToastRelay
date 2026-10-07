using System.Text.Json.Serialization;

namespace WinToastRelay.Models;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RelaySettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(List<PendingDelivery>))]
[JsonSerializable(typeof(List<DeadLetterDelivery>))]
[JsonSerializable(typeof(List<ActivityEntry>))]
public partial class AppJsonContext : JsonSerializerContext;
