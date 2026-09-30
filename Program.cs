using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantERP.Data;
using RestaurantERP.Models;
using RestaurantERP.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Hosting options (see appsettings.Production.json) ────────────────
var useHttps = builder.Configuration.GetValue("Hosting:UseHttps", true);
var autoMigrate = builder.Configuration.GetValue("Hosting:AutoMigrate", true);
var keyRingPath = builder.Configuration.GetValue<string>("Hosting:KeyRingPath");
// Demo categories/products/tables/orders. OFF by default — a live club should never
// get sample data. Set to true only on a throwaway development database.
var seedDemoData = builder.Configuration.GetValue("Hosting:SeedDemoData", false);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql =>
        {
            // A POS on a LAN must survive a brief switch/NIC hiccup instead of
            // throwing a 500 in the cashier's face.
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
            sql.CommandTimeout(60);
        }));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// ── Data protection ──────────────────────────────────────────────────
// Without a persisted key ring, ASP.NET Core generates fresh keys on every
// restart: every terminal gets logged out and antiforgery tokens break.
// Persist them to disk so a server reboot is invisible to the cashiers.
if (!string.IsNullOrWhiteSpace(keyRingPath))
{
    Directory.CreateDirectory(keyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
        .SetApplicationName("RestaurantERP");
}

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    // On a plain-HTTP LAN the cookie must not be marked Secure or login silently fails
    options.Cookie.SecurePolicy = useHttps
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.None;
});

builder.Services.AddControllersWithViews();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(12);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = useHttps
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.None;
});

builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<BranchService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddHttpContextAccessor();



var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    if (useHttps) app.UseHsts();
}

// Only force HTTPS when a real certificate is in play. On an isolated wired LAN
// running plain HTTP, a redirect here would make every terminal unreachable.
if (useHttps) app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Lets you check the server from any terminal's browser: http://<server>/health
// Written inline rather than via AddDbContextCheck so no extra NuGet package is needed.
app.MapGet("/health", async (ApplicationDbContext db) =>
{
    try
    {
        var ok = await db.Database.CanConnectAsync();
        return ok
            ? Results.Text("Healthy")
            : Results.Text("Unhealthy: cannot reach database", statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Text("Unhealthy: " + ex.Message, statusCode: 503);
    }
}).AllowAnonymous();

// ── Startup migration / seed ────────────────────────────────────────
// Applying migrations automatically is convenient, but on a live POS an unexpected
// schema change on restart is dangerous. Set Hosting:AutoMigrate = false in
// production and run Update-Database deliberately during a maintenance window.
try
{
    await DbSeeder.SeedAsync(app.Services, autoMigrate, seedDemoData);
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Database initialisation failed — the app will not start.");
    throw;
}

app.Run();
