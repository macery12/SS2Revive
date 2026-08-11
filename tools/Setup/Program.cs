namespace SS2Revive.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(value => value.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                InstallerEngine.RunSelfTest();
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
        return 0;
    }
}
