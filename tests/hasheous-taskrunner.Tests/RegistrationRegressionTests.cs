using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication;

namespace hasheous_taskrunner.Tests;

public class RegistrationRegressionTests
{
    [Fact]
    public async Task Initialize_WithForceHostRegistration_TriggersHostCall_WhenAlreadyRegistered()
    {
        using var server = new TestHttpServer(request =>
        {
            if (request.HttpMethod == "POST" && (request.RawUrl ?? string.Empty).StartsWith("/api/v1/TaskWorker/clients", StringComparison.OrdinalIgnoreCase))
            {
                return (200, "{\"client_id\":\"client-2\",\"client_api_key\":\"worker-2\"}");
            }

            return (404, "{}");
        });

        TestStateReset.ResetGlobalState(server.BaseUrl + "/", "bootstrap-key");
        Config.LoadConfiguration();

        Common.SetRegistrationInfo(new Dictionary<string, string>
        {
            ["client_id"] = "client-1",
            ["client_api_key"] = "worker-1"
        });

        await Registration.Initialize(
            new Dictionary<string, object>(),
            terminateOnExhaustedRetries: false,
            forceHostRegistration: true);

        bool observedRequest = await server.WaitForRequestsAsync(1, TimeSpan.FromSeconds(2));
        Assert.True(observedRequest);
        Assert.Contains(server.Paths, path => path.StartsWith("/api/v1/TaskWorker/clients", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Initialize_WithoutForceHostRegistration_DoesNotCallHost_WhenAlreadyRegistered()
    {
        using var server = new TestHttpServer(_ => (200, "{\"client_id\":\"client-2\",\"client_api_key\":\"worker-2\"}"));

        TestStateReset.ResetGlobalState(server.BaseUrl + "/", "bootstrap-key");
        Config.LoadConfiguration();

        Common.SetRegistrationInfo(new Dictionary<string, string>
        {
            ["client_id"] = "client-1",
            ["client_api_key"] = "worker-1"
        });

        await Registration.Initialize(
            new Dictionary<string, object>(),
            terminateOnExhaustedRetries: false,
            forceHostRegistration: false);

        await Task.Delay(200);
        Assert.Equal(0, server.RequestCount);
    }
}
