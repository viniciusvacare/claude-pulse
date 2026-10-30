namespace ClaudePulse;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ClaudePulse.SingleInstance", out bool isFirstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudePulse.Show");
        using var historySignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudePulse.History");
        bool history = args.Contains("--history");
        if (!isFirstInstance)
        {
            // Já está rodando: abrir o app de novo só traz o painel (ou o histórico).
            (history ? historySignal : showSignal).Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(showSignal, historySignal, showOnStart: args.Contains("--show"), historyOnStart: history));
    }
}
