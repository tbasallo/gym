using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gym.Web;

internal static class Startup
{
    /// <summary>Key Vault secret name, and the configuration key it is loaded into.</summary>
    private static readonly (string Secret, string Key, bool Required)[] Secrets =
    [
        ("DbConnectionString", "ConnectionStrings:Default", true),
        ("YouTubeApiKey", "YouTube:ApiKey", false),
        ("GymAllowedEmails", "Registration:AllowedEmails", false),
    ];

    /// <summary>
    /// Reads only the secrets this app needs (Get permission only, no List), using the
    /// App Service managed identity in Azure or your Azure CLI login locally.
    /// </summary>
    public static void AddGymKeyVaultSecrets(this ConfigurationManager config)
    {
        var uri = config["KeyVault:Uri"];
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = config["KeyVault:ManagedIdentityClientId"],
        });
        var client = new SecretClient(new Uri(uri), credential);
        var values = new Dictionary<string, string?>();
        foreach (var (secret, key, required) in Secrets)
        {
            try
            {
                values[key] = client.GetSecret(secret).Value.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404 && !required)
            {
                // Optional secret not created yet.
            }
        }

        config.AddInMemoryCollection(values);
    }

    public static DbContextOptionsBuilder UseGymDatabase(this DbContextOptionsBuilder options, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' not found (Key Vault secret 'DbConnectionString').");

        return IsSqlite(config)
            ? options.UseSqlite(connectionString)
            : options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    }

    /// <summary>Applies migrations (SQL Server) or creates the local SQLite file, then seeds exercises.</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:InitializeOnStartup", true))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (IsSqlite(app.Configuration))
        {
            await db.Database.EnsureCreatedAsync();
        }
        else
        {
            await db.Database.MigrateAsync();
        }

        await SeedData.SeedAsync(db);
    }

    private static bool IsSqlite(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
}
