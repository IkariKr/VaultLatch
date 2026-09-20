using VaultLatch;

internal static class Program
{
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "VaultLatch.Tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var stableDirectory = Path.Combine(root, "stable");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "VaultLatch.exe"), "apphost");
            File.WriteAllText(Path.Combine(sourceDirectory, "VaultLatch.dll"), "managed assembly");
            File.WriteAllText(Path.Combine(sourceDirectory, "VaultLatch.deps.json"), "{}");
            File.WriteAllText(Path.Combine(sourceDirectory, "VaultLatch.runtimeconfig.json"), "{}");
            File.WriteAllText(Path.Combine(sourceDirectory, "System.Private.CoreLib.dll"), "runtime");
            Directory.CreateDirectory(Path.Combine(sourceDirectory, "zh-Hans"));
            File.WriteAllText(Path.Combine(sourceDirectory, "zh-Hans", "VaultLatch.resources.dll"), "resource");

            Watchdog.CopyApplicationFiles(Path.Combine(sourceDirectory, "VaultLatch.exe"), stableDirectory);

            AssertFileExists(Path.Combine(stableDirectory, "VaultLatch.exe"));
            AssertFileExists(Path.Combine(stableDirectory, "VaultLatch.dll"));
            AssertFileExists(Path.Combine(stableDirectory, "VaultLatch.deps.json"));
            AssertFileExists(Path.Combine(stableDirectory, "VaultLatch.runtimeconfig.json"));
            AssertFileExists(Path.Combine(stableDirectory, "System.Private.CoreLib.dll"));
            AssertFileExists(Path.Combine(stableDirectory, "zh-Hans", "VaultLatch.resources.dll"));
            Assert(
                Watchdog.AreApplicationFilesCurrent(Path.Combine(sourceDirectory, "VaultLatch.exe"), stableDirectory),
                "Copied application should be current.");
            File.WriteAllText(Path.Combine(stableDirectory, "LifeOSGuard.exe"), "legacy apphost");
            Assert(
                Watchdog.AreApplicationFilesCurrent(Path.Combine(sourceDirectory, "VaultLatch.exe"), stableDirectory),
                "Legacy files in the stable directory must not force a refresh.");

            File.AppendAllText(Path.Combine(sourceDirectory, "VaultLatch.dll"), "changed");
            Assert(
                !Watchdog.AreApplicationFilesCurrent(Path.Combine(sourceDirectory, "VaultLatch.exe"), stableDirectory),
                "Changed application should require refresh.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertFileExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Expected copied file: {path}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
