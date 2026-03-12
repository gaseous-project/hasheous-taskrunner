using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using hasheous_taskrunner.Classes.Communication;

namespace hasheous_taskrunner.Tests;

public class UpdaterRegressionTests
{
    [Fact]
    public async Task MissingChecksum_IsBlocked_WhenAllowInsecureUpdateIsFalse()
    {
        using var server = new TestHttpServer(_ => (200, "binary"));

        TestStateReset.ResetGlobalState(server.BaseUrl + "/", "bootstrap-key", allowInsecureUpdate: "false");
        _ = hasheous_taskrunner.Classes.Config.Configuration;

        object release = CreateRelease(
            tag: "v9.9.9",
            executableAssetUrl: server.BaseUrl + "/download/bin",
            includeChecksumAsset: false,
            checksumAssetUrl: null);

        await InvokeDownloadAndApplyUpdateAsync(release);

        await Task.Delay(200);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task MissingChecksum_ProceedsToDownload_WhenAllowInsecureUpdateIsTrue()
    {
        using var server = new TestHttpServer(request =>
        {
            if ((request.RawUrl ?? string.Empty).Contains("/download/bin", StringComparison.OrdinalIgnoreCase))
            {
                // Force download failure after integrity gate has been bypassed.
                return (500, "download failed");
            }

            return (404, "not found");
        });

        TestStateReset.ResetGlobalState(server.BaseUrl + "/", "bootstrap-key", allowInsecureUpdate: "true");
        _ = hasheous_taskrunner.Classes.Config.Configuration;

        object release = CreateRelease(
            tag: "v9.9.9",
            executableAssetUrl: server.BaseUrl + "/download/bin",
            includeChecksumAsset: false,
            checksumAssetUrl: null);

        await InvokeDownloadAndApplyUpdateAsync(release);

        bool observedDownloadAttempt = await server.WaitForRequestsAsync(1, TimeSpan.FromSeconds(2));
        Assert.True(observedDownloadAttempt);
        Assert.Contains(server.Paths, path => path.Contains("/download/bin", StringComparison.OrdinalIgnoreCase));
    }

    private static object CreateRelease(string tag, string executableAssetUrl, bool includeChecksumAsset, string? checksumAssetUrl)
    {
        Type updaterType = typeof(Updater);
        Type releaseType = updaterType.GetNestedType("GitHubRelease", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate Updater.GitHubRelease type.");
        Type assetType = updaterType.GetNestedType("GitHubAsset", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate Updater.GitHubAsset type.");

        object release = Activator.CreateInstance(releaseType)
            ?? throw new InvalidOperationException("Unable to create release object.");

        releaseType.GetProperty("Tag")?.SetValue(release, tag);
        releaseType.GetProperty("Prerelease")?.SetValue(release, false);

        var assets = (IList)(Activator.CreateInstance(typeof(List<>).MakeGenericType(assetType))
            ?? throw new InvalidOperationException("Unable to create assets list."));

        assets.Add(CreateAsset(assetType, BuildExecutableAssetName(tag), executableAssetUrl));

        if (includeChecksumAsset && !string.IsNullOrWhiteSpace(checksumAssetUrl))
        {
            assets.Add(CreateAsset(assetType, BuildExecutableAssetName(tag) + ".sha256", checksumAssetUrl));
        }

        releaseType.GetProperty("Assets")?.SetValue(release, assets);
        return release;
    }

    private static object CreateAsset(Type assetType, string name, string downloadUrl)
    {
        object asset = Activator.CreateInstance(assetType)
            ?? throw new InvalidOperationException("Unable to create release asset object.");
        assetType.GetProperty("Name")?.SetValue(asset, name);
        assetType.GetProperty("BrowserDownloadUrl")?.SetValue(asset, downloadUrl);
        return asset;
    }

    private static string BuildExecutableAssetName(string tag)
    {
        string version = tag.TrimStart('v');
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        if (OperatingSystem.IsWindows())
        {
            return $"hasheous-taskrunner-windows-{version}-{arch}.exe";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"hasheous-taskrunner-linux-{version}-{arch}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"hasheous-taskrunner-macos-{version}-{arch}";
        }

        throw new PlatformNotSupportedException("Unsupported OS for updater test.");
    }

    private static async Task InvokeDownloadAndApplyUpdateAsync(object release)
    {
        Type updaterType = typeof(Updater);
        MethodInfo? method = updaterType.GetMethod("DownloadAndApplyUpdate", BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
        {
            throw new InvalidOperationException("Could not find private method Updater.DownloadAndApplyUpdate.");
        }

        object? taskObj = method.Invoke(null, new[] { release });
        if (taskObj is not Task task)
        {
            throw new InvalidOperationException("Updater.DownloadAndApplyUpdate did not return a Task.");
        }

        await task;
    }
}
