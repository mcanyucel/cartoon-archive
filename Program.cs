using CartoonArchive.Api;
using CartoonArchive.Auth;
using CartoonArchive.Captioning;
using CartoonArchive.Configuration;
using CartoonArchive.Data;
using CartoonArchive.Sharing;
using CartoonArchive.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Custom Options
builder.Services.Configure<CaptionOptions>(builder.Configuration.GetSection("Caption"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<ShareOptions>(
    builder.Configuration.GetSection(ShareOptions.SectionName)
);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient<ICaptionService, OpenAiCaptionService>(
    (sp, http) =>
    {
        var options = sp.GetRequiredService<IOptions<CaptionOptions>>().Value;
        http.BaseAddress = new Uri(options.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(60);
    }
);
builder
    .Services.AddHttpClient<UrlImageFetcher>(http =>
    {
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ComicArchive/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = SsrfGuard.SafeConnectAsync,
        }
    );

// Add DbContext
builder.Services.AddDbContext<CartoonArchiveDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default"))
);

// Custom Services
builder.Services.AddSingleton<ImageStorage>();
builder.Services.AddSingleton<ThumbnailGenerator>();
builder.Services.AddHostedService<CaptionWorker>();
builder.Services.AddHostedService<ShareCleanupWorker>();
builder.Services.AddSingleton<PerceptualHasher>();

// Ensure required directories exist
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "images"));
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "thumbnails"));

// Persist DataProtection keys so antiforgery tokens and auth cookies survive container recreation.
var dpKeysPath = Path.Combine(builder.Environment.ContentRootPath, "data", "dp-keys");
Directory.CreateDirectory(dpKeysPath);
builder
    .Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
    .SetApplicationName("CartoonArchive");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

var appOpts = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>();
if (appOpts?.TrustProxyHeaders == true)
{
    var fwd = new ForwardedHeadersOptions
    {
        ForwardedHeaders =
            ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost,
    };
    // Clear the default localhost-only allowlist - the app should be only reachable
    // via the proxy on an internal connection, so any upstream is trusted.
    fwd.KnownIPNetworks.Clear();
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}
app.UseHttpsRedirection();

app.UseMiddleware<ReverseProxyAuthMiddleware>();

app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
            Path.Combine(app.Environment.ContentRootPath, "data", "images")
        ),
        RequestPath = "/images",
    }
);
app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(
            Path.Combine(app.Environment.ContentRootPath, "data", "thumbnails")
        ),
        RequestPath = "/thumbnails",
    }
);

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapImageEndpoints();
app.MapShareEndpoints();

// Apply any pending migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
