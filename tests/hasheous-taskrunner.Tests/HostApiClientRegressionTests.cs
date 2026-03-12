using System.Reflection;
using hasheous_taskrunner.Classes.Communication.Clients;

namespace hasheous_taskrunner.Tests;

public class HostApiClientRegressionTests
{
    [Fact]
    public async Task Rejects_NonHost_Absolute_Urls()
    {
        var client = new HostApiClient("https://hasheous.org/");
        client.SetBootstrapApiKey("bootstrap-key");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync<Dictionary<string, string>>("https://example.com/api/v1/TaskWorker/clients"));
    }

    [Fact]
    public async Task Uses_RequestScoped_AuthHeaders_Without_DefaultHeaderMutation()
    {
        using var server = new TestHttpServer(request =>
        {
            bool hasBootstrapHeader = request.Headers.AllKeys.Contains("X-API-Key");
            bool hasWorkerHeader = request.Headers.AllKeys.Contains("X-TaskWorker-API-Key");
            string body = $"{{\"sawBootstrap\":{hasBootstrapHeader.ToString().ToLowerInvariant()},\"sawWorker\":{hasWorkerHeader.ToString().ToLowerInvariant()}}}";
            return (200, body);
        });

        var client = new HostApiClient(server.BaseUrl);
        client.SetBootstrapApiKey("bootstrap-key");

        var response = await client.GetAsync<Dictionary<string, bool>>("/api/v1/TaskWorker/health");

        HttpClient httpClient = GetInternalHttpClient(client);
        bool hasDefaultBootstrap = httpClient.DefaultRequestHeaders.Contains("X-API-Key");
        bool hasDefaultWorker = httpClient.DefaultRequestHeaders.Contains("X-TaskWorker-API-Key");

        Assert.NotNull(response);
        Assert.True(response!["sawBootstrap"]);
        Assert.False(response["sawWorker"]);
        Assert.False(hasDefaultBootstrap);
        Assert.False(hasDefaultWorker);
    }

    [Fact]
    public async Task AutoAuth_PrefersWorkerHeader_WhenBootstrapAndWorkerKeysExist()
    {
        using var server = new TestHttpServer(request =>
        {
            bool hasBootstrapHeader = request.Headers.AllKeys.Contains("X-API-Key");
            bool hasWorkerHeader = request.Headers.AllKeys.Contains("X-TaskWorker-API-Key");
            string body = $"{{\"sawBootstrap\":{hasBootstrapHeader.ToString().ToLowerInvariant()},\"sawWorker\":{hasWorkerHeader.ToString().ToLowerInvariant()}}}";
            return (200, body);
        });

        var client = new HostApiClient(server.BaseUrl);
        client.SetRegistrationInfo("client-1", "worker-key");
        client.SetBootstrapApiKey("bootstrap-key");

        var response = await client.GetAsync<Dictionary<string, bool>>("/api/v1/TaskWorker/health");

        Assert.NotNull(response);
        Assert.False(response!["sawBootstrap"]);
        Assert.True(response["sawWorker"]);
    }

    private static HttpClient GetInternalHttpClient(HostApiClient client)
    {
        FieldInfo? field = typeof(HostApiClient).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var value = field!.GetValue(client) as HttpClient;
        Assert.NotNull(value);
        return value!;
    }
}
