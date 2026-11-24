using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
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
builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<UpdateCheck>();
builder.Services.AddSingleton<Analytics>();
// unhandled errors as an Umami event (Analytics)
builder.Services.AddSingleton<ILoggerProvider, ErrorReporter>();

var host = builder.Build();
host.Services.GetRequiredService<Analytics>();   // subscribes to the store and the plan runner
await host.RunAsync();
