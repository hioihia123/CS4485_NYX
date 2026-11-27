using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NyxInsightsWeb.Data;
using NyxInsightsWeb.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<AzureMlPredictionService>();
builder.Services.AddScoped<FoundryAgentService>();
builder.Services.AddScoped<GoogleHealthAuthService>();
builder.Services.AddScoped<GoogleHealthDataService>();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddRazorPages();

builder.Services.AddControllersWithViews();

builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromSeconds(10);
    //options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.UseSession();


app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
    
app.MapControllers();
app.MapRazorPages();
app.Run();
