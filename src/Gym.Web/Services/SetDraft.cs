namespace Gym.Web.Services;

/// <summary>What a person is about to log (weight and reps inputs), kept per device.</summary>
public sealed class SetDraft
{
    private decimal _weight;

    /// <summary>Stored without trailing zeros so inputs show "65" rather than "65.00".</summary>
    public decimal Weight
    {
        get => _weight;
        set => _weight = value / 1.000000000000000000000000000000000m;
    }

    public int Reps { get; set; }
}
