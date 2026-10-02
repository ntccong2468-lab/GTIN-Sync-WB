using MarketplaceHub.Core;
namespace MarketplaceHub.Workflows;
public sealed class FakeWorkflowClock : IWorkflowClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-02T20:00:00Z");
    public List<TimeSpan> Delays { get; } = new();
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Delays.Add(delay); UtcNow += delay; return Task.CompletedTask; }
}
