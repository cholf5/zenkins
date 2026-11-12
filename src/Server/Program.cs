using Zenkins.Server.Components;
using Zenkins.Server.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddZenkinsData(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
