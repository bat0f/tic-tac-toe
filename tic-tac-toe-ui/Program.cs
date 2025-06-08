using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace tic_tac_toe_ui
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebAssemblyHostBuilder.CreateDefault(args);
            builder.RootComponents.Add<App>("#app");
            builder.RootComponents.Add<HeadOutlet>("head::after");

            // Настраиваем HttpClient с перехватчиком для добавления токена
            builder.Services.AddScoped<CustomHttpClientHandler>();
            builder.Services.AddHttpClient("TicTacToeApi", client =>
            {
                client.BaseAddress = new Uri("https://26.171.188.146:5001/");
            }).AddHttpMessageHandler<CustomHttpClientHandler>();

            builder.Services.AddScoped(sp => new HttpClient(sp.GetRequiredService<CustomHttpClientHandler>())
            {
                BaseAddress = new Uri("https://26.171.188.146:5001/")
            });

            builder.Services.AddMudServices();

            // Настраиваем SignalR с передачей токена
            builder.Services.AddScoped(sp =>
            {
                var jsRuntime = sp.GetRequiredService<IJSRuntime>();
                return new HubConnectionBuilder()
                    .WithUrl("https://26.171.188.146:5001/gameHub", options =>
                    {
                        options.AccessTokenProvider = async () =>
                        {
                            var token = await jsRuntime.InvokeAsync<string>("localStorage.getItem", "accessToken");
                            Console.WriteLine($"Program.cs: Providing AccessToken for SignalR={token}");
                            return token;
                        };
                    })
                    .WithAutomaticReconnect()
                    .Build();
            });

            await builder.Build().RunAsync();
        }
    }

    // Класс для перехвата запросов и добавления токена
    public class CustomHttpClientHandler : DelegatingHandler
    {
        private readonly IJSRuntime _jsRuntime;

        public CustomHttpClientHandler(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
            InnerHandler = new HttpClientHandler();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var accessToken = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "accessToken");
            Console.WriteLine($"CustomHttpClientHandler: Sending request to {request.RequestUri}, AccessToken={accessToken}");
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.Headers.Add("Authorization", $"Bearer {accessToken}");
            }
            var response = await base.SendAsync(request, cancellationToken);
            Console.WriteLine($"CustomHttpClientHandler: Response status for {request.RequestUri} = {response.StatusCode}");
            return response;
        }
    }
}