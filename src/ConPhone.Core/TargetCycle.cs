namespace ConPhone.Core;

public static class TargetCycle
{
    public static string? Next(
        IReadOnlyList<string> registrationOrder,
        IReadOnlySet<string> readyIds,
        string? currentId)
    {
        ArgumentNullException.ThrowIfNull(registrationOrder);
        ArgumentNullException.ThrowIfNull(readyIds);

        var ready = registrationOrder
            .Where(id =>
                !string.IsNullOrWhiteSpace(id) &&
                readyIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ready.Length == 0)
            return null;

        if (currentId is null)
            return ready[0];

        var index = Array.FindIndex(
            ready,
            id => string.Equals(
                id,
                currentId,
                StringComparison.Ordinal));

        return index < 0
            ? ready[0]
            : ready[(index + 1) % ready.Length];
    }
}
