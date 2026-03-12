using System.Reflection;
using hasheous_taskrunner.Classes;
using hasheous_taskrunner.Classes.Communication;
using hasheous_taskrunner.Classes.Tasks;

namespace hasheous_taskrunner.Tests;

internal static class TestStateReset
{
    public static void ResetGlobalState(string hostAddress, string apiKey, string? allowInsecureUpdate = null)
    {
        Environment.SetEnvironmentVariable("HostAddress", hostAddress);
        Environment.SetEnvironmentVariable("APIKey", apiKey);
        if (allowInsecureUpdate != null)
        {
            Environment.SetEnvironmentVariable("AllowInsecureUpdate", allowInsecureUpdate);
        }

        ResetConfigState();
        ResetCommonState();
        ResetRegistrationState();
        ResetTasksState();
    }

    private static void ResetConfigState()
    {
        Type configType = typeof(Config);

        SetPrivateStaticField(configType, "currentConfig", new Dictionary<string, string>());
        SetPrivateStaticField(configType, "authData", new Dictionary<string, string>());
    }

    private static void ResetCommonState()
    {
        Type commonType = typeof(Common);

        SetPrivateStaticField(commonType, "_hostApiClient", null);
        SetPrivateStaticField(commonType, "registrationInfo", new Dictionary<string, string>());
    }

    private static void ResetRegistrationState()
    {
        Type registrationType = typeof(Registration);

        SetPrivateStaticField(registrationType, "lastRegistrationTime", DateTime.MinValue);
        SetPrivateStaticField(registrationType, "healthState", RegistrationHealthState.Degraded);
        SetPrivateStaticField(registrationType, "recoveryTask", null);
    }

    private static void ResetTasksState()
    {
        Type tasksType = typeof(hasheous_taskrunner.Classes.Communication.Tasks);
        SetPrivateStaticField(tasksType, "lastTaskFetch", DateTime.MinValue);
        SetPrivateStaticField(tasksType, "lastBlockedIntakeLog", DateTime.MinValue);
        SetPrivateStaticField(tasksType, "_MaxConcurrentTasks", 1);

        FieldInfo? activeField = tasksType.GetField("activeTaskExecutors", BindingFlags.NonPublic | BindingFlags.Static);
        if (activeField == null)
        {
            throw new InvalidOperationException("Field 'activeTaskExecutors' not found on Tasks.");
        }

        object? activeValue = activeField.GetValue(null);
        if (activeValue is System.Collections.IDictionary map)
        {
            map.Clear();
            return;
        }

        throw new InvalidOperationException("Unable to clear activeTaskExecutors map.");
    }

    private static void SetPrivateStaticField(Type type, string fieldName, object? value)
    {
        FieldInfo? field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on type '{type.FullName}'.");
        }

        field.SetValue(null, value);
    }
}
