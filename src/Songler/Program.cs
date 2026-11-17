using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;
using Songler;
using Songler.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices(c =>
{
    c.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    c.SnackbarConfiguration.ShowTransitionDuration = 150;
    c.SnackbarConfiguration.VisibleStateDuration = 4000;
});
builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<MetronomeService>();
builder.Services.AddSingleton<Shortcuts>();
builder.Services.AddSingleton<ScoreImport>();

await builder.Build().RunAsync();
