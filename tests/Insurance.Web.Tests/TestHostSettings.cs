using System.Runtime.CompilerServices;

namespace Insurance.Web.Tests;

/// <summary>
/// Settings for every application the web tests start. Each test class starts its own host, and a host watches its
/// settings files for changes; with many hosts at once the system's file-watch limit (inotify instances, 128 by default
/// on Linux) runs out and a host fails to start. The tests never change those files, so the hosts do not reload on a
/// change and any watcher left polls instead of holding a system watch.
/// </summary>
internal static class TestHostSettings
{
    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }
}
