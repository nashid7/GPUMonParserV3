using par.Components;
using par.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddScoped<NotificationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

var uploadsRoot = Path.Combine(app.Environment.WebRootPath, "uploads");
Directory.CreateDirectory(uploadsRoot);

app.MapPost("api/upload/save", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null) return Results.BadRequest();
    var temp = Path.Combine(app.Environment.WebRootPath, file.FileName);
    await using var fs = File.Create(temp);
    await file.CopyToAsync(fs);
    return Results.Ok();
});

app.MapPost("api/upload/remove", async (HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var name = form.Files.FirstOrDefault()?.FileName;
    if (!string.IsNullOrEmpty(name))
    {
        var temp = Path.Combine(app.Environment.WebRootPath, name);
        if (File.Exists(temp)) File.Delete(temp);
    }

    return Results.Ok();
});

app.Run();
