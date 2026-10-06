using Gym.Web;
using Gym.Web.Components;
using Gym.Web.Components.Account;
using Gym.Web.Data;
using Gym.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Secrets (connection string, API keys) come from Azure Key Vault when KeyVault:Uri is set.
builder.Configuration.AddGymKeyVaultSecrets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(60);
    options.SlidingExpiration = true;
});

builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseGymDatabase(builder.Configuration));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddScoped<RegistrationPolicy>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddScoped<PlaceholderService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<ExerciseService>();
builder.Services.AddScoped<SampleDataService>();
builder.Services.AddHttpClient<YouTubeClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient<WgerClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("GymTracker/1.0 (+https://github.com/tbasallo/gym)");
});

var app = builder.Build();

await app.InitializeDatabaseAsync();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
