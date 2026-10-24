
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NyxInsightsWeb.Data;

var builder = WebApplication.CreateBuilder(args);

// ==========================================
// 1. AZURE POSTGRESQL DATABASE
// ==========================================

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// ==========================================
// 2. ASP.NET CORE IDENTITY
// ==========================================

// Preserve existing login, registration,
// password hashing, and database integration.

builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();

// ==========================================
// 3. MVC AND RAZOR PAGES
// ==========================================

builder.Services.AddRazorPages();
builder.Services.AddControllersWithViews();

// ==========================================
// 4. AZURE MACHINE LEARNING
// ==========================================

// Register a named HTTP client for Azure ML.
// The API key is read server-side by
// NyxPredictionController from configuration.

builder.Services.AddHttpClient("NyxAzureMl", client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
});

// ==========================================
// 5. SESSION CONFIGURATION
// ==========================================

builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromSeconds(10);

    // Preserve existing session settings.
    // Session cookie HttpOnly defaults to true.
    // options.Cookie.HttpOnly = true;

    options.Cookie.IsEssential = true;
});

// ==========================================
// 6. BUILD APPLICATION
// ==========================================

var app = builder.Build();

// ==========================================
// 7. ERROR HANDLING
// ==========================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// ==========================================
// 8. MIDDLEWARE
// ==========================================

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Preserve authentication and authorization.
app.UseAuthentication();
app.UseAuthorization();

// Preserve session functionality.
app.UseSession();

// ==========================================
// 9. MVC ROUTES
// ==========================================

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ==========================================
// 10. AZURE ML API ROUTES
// ==========================================

// Enable attribute-routed API controllers.
// Example:
// POST /api/nyx/predict

app.MapControllers();

// ==========================================
// 11. IDENTITY RAZOR PAGES
// ==========================================

// Preserve ASP.NET Identity login and signup.
// /Identity/Account/Login
// /Identity/Account/Register

app.MapRazorPages();

// ==========================================
// 12. START APPLICATION
// ==========================================

app.Run();

