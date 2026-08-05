using CartoonArchive.Api;
using CartoonArchive.Captioning;
using CartoonArchive.Data;
using CartoonArchive.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Custom Options
builder.Services.Configure<CaptionOptions>(builder.Configuration.GetSection("Caption"));

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

// Add DbContext
builder.Services.AddDbContext<CartoonArchiveDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default"))
);

// Custom Services
builder.Services.AddSingleton<ImageStorage>();
builder.Services.AddSingleton<ThumbnailGenerator>();
builder.Services.AddHostedService<CaptionWorker>();

// Ensure required directories exist
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "images"));
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data", "thumbnails"));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

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

// Apply any pending migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CartoonArchiveDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
