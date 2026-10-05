using System.Text.RegularExpressions;
using Gym.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Gym.Tests;

/// <summary>
/// Tables must not be schema-qualified: SQL Server puts them in the signed-in
/// database user's default schema.
/// </summary>
public class SchemaTests
{
    private static ApplicationDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=.;Database=SchemaCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);

    [Fact]
    public void Model_has_no_schemas()
    {
        using var db = SqlServerContext();
        Assert.Null(db.Model.GetDefaultSchema());
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Null(e.GetSchema()));
    }

    [Fact]
    public void Migration_sql_has_no_schema_qualifiers()
    {
        using var db = SqlServerContext();
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
        var sql = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);

        Assert.Contains("CREATE TABLE [AspNetUsers]", sql);
        Assert.DoesNotMatch(new Regex(@"\bdbo\b|EnsureSchema|CREATE SCHEMA|\]\s*\.\s*\[", RegexOptions.IgnoreCase), sql);
    }
}
