using Gym.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gym.Tests;

/// <summary>In-memory SQLite database shared by every context the factory creates.</summary>
public sealed class TestDb : IDbContextFactory<ApplicationDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
        SeedData.SeedAsync(db).GetAwaiter().GetResult();
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public ApplicationUser AddUser(string name)
    {
        using var db = CreateDbContext();
        var user = new ApplicationUser
        {
            UserName = $"{name.ToLowerInvariant()}@example.com",
            Email = $"{name.ToLowerInvariant()}@example.com",
            NormalizedEmail = $"{name.ToUpperInvariant()}@EXAMPLE.COM",
            DisplayName = name,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>A clock the test controls.</summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
