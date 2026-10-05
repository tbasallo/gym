using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Gym.Web.Data;

public class ApplicationUser : IdentityUser
{
    [MaxLength(50)]
    public string DisplayName { get; set; } = "";

    /// <summary>Label shown next to weights ("lb" or "kg"). Values are stored as entered.</summary>
    [MaxLength(5)]
    public string WeightUnit { get; set; } = "lb";

    /// <summary>Rest goal shown on the rest timer when an exercise does not set its own.</summary>
    public int DefaultRestSeconds { get; set; } = 90;

    /// <summary>IANA time zone used to display dates; detected from the browser when empty.</summary>
    [MaxLength(64)]
    public string? TimeZoneId { get; set; }

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? (UserName ?? Email ?? "?") : DisplayName;
}
