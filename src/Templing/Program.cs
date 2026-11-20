using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;
using Templing;
using Templing.Services;

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
builder.Services.AddSingleton<PlanRunner>();

await builder.Build().RunAsync();
