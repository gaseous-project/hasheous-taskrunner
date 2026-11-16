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

    [Fact]
    public void CanAcceptNewTask_AllowsSameTaskType()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Start with no tasks - should accept any task
        var taskAI1 = new TaskItem
        {
            Id = 1001,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod!.Invoke(null, new object[] { taskAI1 })!;
        Assert.True(canAccept);

        // Add a task of type AIDescriptionAndTagging
        var executor1 = new TaskExecutor(taskAI1);
        map.TryAdd(1001, executor1);

        // Should accept another AIDescriptionAndTagging task
        var taskAI2 = new TaskItem
        {
            Id = 1002,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskAI2 })!;
        Assert.True(canAccept);

        // Should NOT accept a different type (AILocalisationGeneration) with shared exclusive capability
        var taskAI3 = new TaskItem
        {
            Id = 1003,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskAI3 })!;
        Assert.False(canAccept);

        map.Clear();
    }

    [Fact]
    public void CanAcceptNewTask_BlocksDifferentTaskType()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Add a task of type AILocalisationGeneration with AI capability
        var taskLocal1 = new TaskItem
        {
            Id = 2001,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        var executor1 = new TaskExecutor(taskLocal1);
        map.TryAdd(2001, executor1);

        // Should NOT accept AIDescriptionAndTagging when AILocalisationGeneration is running (shared AI capability)
        var taskTag1 = new TaskItem
        {
            Id = 2002,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskTag1 })!;
        Assert.False(canAccept);

        // Should accept another AILocalisationGeneration task
        var taskLocal2 = new TaskItem
        {
            Id = 2003,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskLocal2 })!;
        Assert.True(canAccept);

        map.Clear();
    }

    [Fact]
    public void CanAcceptNewTask_AllowsDifferentTypesWithoutExclusiveCapabilities()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Add a task of type AIDescriptionAndTagging with only DiskSpace capability (not exclusive, task itself doesn't require exclusivity)
        var taskDisk1 = new TaskItem
        {
            Id = 3001,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        var executor1 = new TaskExecutor(taskDisk1);
        map.TryAdd(3001, executor1);

        // Should accept another AIDescriptionAndTagging with only DiskSpace capability (same type, different task doesn't require exclusivity)
        var taskDisk2 = new TaskItem
        {
            Id = 3002,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskDisk2 })!;
        Assert.True(canAccept);

        map.Clear();
    }

    [Fact]
    public void CanAcceptNewTask_BlocksDifferentTaskTypeWhenRunningTaskRequiresExclusivity()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Add AILocalisationGeneration task (requires exclusive execution)
        var taskExclusive = new TaskItem
        {
            Id = 4001,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        var executor1 = new TaskExecutor(taskExclusive);
        map.TryAdd(4001, executor1);

        // Should NOT accept AIDescriptionAndTagging when AILocalisationGeneration (exclusive task) is running
        var taskNonExclusive = new TaskItem
        {
            Id = 4002,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskNonExclusive })!;
        Assert.False(canAccept);

        map.Clear();
    }

    [Fact]
    public void CanAcceptNewTask_BlocksOtherTaskTypeWhenNewTaskRequiresExclusivity()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Add AIDescriptionAndTagging task (non-exclusive)
        var taskNonExclusive = new TaskItem
        {
            Id = 5001,
            TaskName = TaskType.AIDescriptionAndTagging,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        var executor1 = new TaskExecutor(taskNonExclusive);
        map.TryAdd(5001, executor1);

        // Should NOT accept AILocalisationGeneration (exclusive task) when any other task type is running
        var taskExclusive = new TaskItem
        {
            Id = 5002,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.DiskSpace },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskExclusive })!;
        Assert.False(canAccept);

        map.Clear();
    }

    [Fact]
    public void CanAcceptNewTask_AllowsMultipleSameTypeEvenIfTaskRequiresExclusivity()
    {
        TestStateReset.ResetGlobalState("https://hasheous.org/", "bootstrap-key");
        Config.LoadConfiguration();

        var map = GetActiveExecutorsMap();
        var canAcceptMethod = typeof(hasheous_taskrunner.Classes.Communication.Tasks)
            .GetMethod("CanAcceptNewTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(canAcceptMethod);

        // Add one AILocalisationGeneration task (exclusive)
        var taskExclusive1 = new TaskItem
        {
            Id = 6001,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        var executor1 = new TaskExecutor(taskExclusive1);
        map.TryAdd(6001, executor1);

        // Should accept another AILocalisationGeneration task of the same type (multiple same-type allowed)
        var taskExclusive2 = new TaskItem
        {
            Id = 6002,
            TaskName = TaskType.AILocalisationGeneration,
            RequiredCapabilities = new List<hasheous_taskrunner.Classes.Tasks.Capabilities> { hasheous_taskrunner.Classes.Tasks.Capabilities.AI },
            Parameters = new Dictionary<string, string>()
        };
        bool canAccept = (bool)canAcceptMethod.Invoke(null, new object[] { taskExclusive2 })!;
        Assert.True(canAccept);

        map.Clear();
    }
}
