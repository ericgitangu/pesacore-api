using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PesaCore.Web.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same-origin HttpClient: BaseAddress is the host origin, so "api/..." calls
// hit the BFF, which proxies them to PesaCore. No CORS, no API URL in the browser.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<PesaCore.Web.Client.Services.PesaCoreApi>();

await builder.Build().RunAsync();
