using System.Reflection;
using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication;
using hasheous_taskrunner.Classes.Tasks;

namespace hasheous_taskrunner.Tests;

public class TasksRegressionTests
{
    [Fact]
    public async Task FetchAndExecuteTasksIfDue_DoesNotOverlapConcurrentFetchCycles()
    {
        using var server = new TestHttpServer(request =>
        {
            if (request.HttpMethod == "GET" && (request.RawUrl ?? string.Empty).Contains("/api/v1/TaskWorker/clients/", StringComparison.OrdinalIgnoreCase))
            {
                return (200, "[]");
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

        var runs = Enumerable.Range(0, 12)
            .Select(_ => hasheous_taskrunner.Classes.Communication.Tasks.FetchAndExecuteTasksIfDue())
            .ToArray();

        await Task.WhenAll(runs);

        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public void GetActiveTaskExecutorsSnapshot_IsStableAcrossConcurrentMutations()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();

        var firstExecutor = new TaskExecutor(new TaskItem
        {
            Id = 1001,
            TaskName = TaskType.AIDescriptionAndTagging,
            Parameters = new Dictionary<string, string>()
        });
        map.TryAdd(1001, firstExecutor);

        var snapshot = hasheous_taskrunner.Classes.Communication.Tasks.GetActiveTaskExecutorsSnapshot();

        var secondExecutor = new TaskExecutor(new TaskItem
        {
            Id = 1002,
            TaskName = TaskType.AIDescriptionAndTagging,
            Parameters = new Dictionary<string, string>()
        });
        map.TryAdd(1002, secondExecutor);
        map.TryRemove(1001, out _);

        int enumerated = 0;
        foreach (var _ in snapshot)
        {
            enumerated++;
        }

        Assert.Single(snapshot);
        Assert.Equal(1, enumerated);
        Assert.True(snapshot.ContainsKey(1001));
        Assert.False(snapshot.ContainsKey(1002));
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<long, TaskExecutor> GetActiveExecutorsMap()
    {
        FieldInfo? field = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetField("activeTaskExecutors", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var value = field!.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<long, TaskExecutor>;
        Assert.NotNull(value);
        return value!;
    }
}
