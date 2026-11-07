namespace FitzLauncher.Core.Optimization;

public readonly record struct ThermalSample(double? TempC, double? Load, double? PowerW);

/// <summary>
/// "Why is it hot?": looks at temperature against load over the last few minutes.
/// Hot while working hard = the chip is doing a lot (lower the cap/power, or a faster fan curve).
/// Hot while barely working = a cooling problem (dust, blocked vents, warm room, lazy fans).
/// </summary>
public static class ThermalDiagnosis
{
    public static string Explain(string component, IReadOnlyList<ThermalSample> samples, double limitC)
    {
        var recent = samples.TakeLast(60).Where(s => s.TempC is not null).ToList();
        if (recent.Count < 5) return $"{component} is above your {limitC:0} °C limit.";

        var loads = recent.Where(s => s.Load is not null).Select(s => s.Load!.Value).ToList();
        var avgLoad = loads.Count > 0 ? loads.Average() : (double?)null;
        var first = recent.Take(recent.Count / 3).Average(s => s.TempC!.Value);
        var last = recent.Skip(recent.Count * 2 / 3).Average(s => s.TempC!.Value);
        var rising = last - first > 3;

        if (avgLoad is >= 85)
            return $"{component} is working flat out ({avgLoad:0}% load){(rising ? " and still heating up" : "")}. A lower FPS cap or power limit cuts the heat; a more aggressive fan curve also helps.";
        if (avgLoad is < 60)
            return $"{component} is hot even at {avgLoad:0}% load, which points to cooling rather than the game: dust or blocked vents, a warm room, or a fan curve that's too relaxed.";
        return $"{component} is under steady load ({avgLoad?.ToString("0") ?? "?"}%){(rising ? " and heating up" : "")}. Lowering the cap a step usually brings it back under {limitC:0} °C.";
    }
}
