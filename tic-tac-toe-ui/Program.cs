
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;

namespace tic_tac_toe_ui
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");

            builder.Services.AddScoped(sp =>
    new HubConnectionBuilder()
        .WithUrl("https://26.171.188.146:5001/gameHub", options =>
        {
            options.HttpMessageHandlerFactory = handler => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            options.Headers.Add("X-Custom-Header", "value"); // если надо
            options.UseDefaultCredentials = true; // 🟢 Это аналог withCredentials = true
        }).Build());

            await builder.Build().RunAsync();
        }
    }
}
