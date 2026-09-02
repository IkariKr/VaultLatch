using System.Threading;

namespace VaultLatch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\VaultLatch.Singleton", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new GuardApplicationContext());
    }
}
