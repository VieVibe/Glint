using System.Windows;
using Glint.App.Services;

namespace Glint.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showSignal;
    private EventWaitHandle? exitSignal;
    private Thread? signalThread;
    private volatile bool stopping;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => AppDiagnostics.Log("Unhandled UI exception", args.Exception);
        if (e.Args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\Glint.Launcher.Exit", out var signal))
            { using (signal) signal.Set(); }
            Shutdown();
            return;
        }
        instanceMutex = new Mutex(true, @"Local\Glint.Launcher.Instance", out ownsMutex);
        if (!ownsMutex)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\Glint.Launcher.Show"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glint.Launcher.Show");
        exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glint.Launcher.Exit");
        var window = new MainWindow();
        MainWindow = window;
        signalThread = new Thread(() =>
        {
            WaitHandle[] signals = [exitSignal, showSignal];
            while (true)
            {
                var index = WaitHandle.WaitAny(signals);
                if (stopping || Dispatcher.HasShutdownStarted) return;
                try
                {
                    if (index == 0) { Dispatcher.BeginInvoke(window.RequestExit); return; }
                    Dispatcher.BeginInvoke(window.ShowLauncher);
                }
                catch (InvalidOperationException) when (stopping || Dispatcher.HasShutdownStarted) { return; }
            }
        }) { IsBackground = true, Name = "Glint instance signal" };
        signalThread.Start();
        window.InitializeLauncher(e.Args.Contains("--background"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        stopping = true;
        exitSignal?.Set();
        signalThread?.Join();
        showSignal?.Dispose();
        exitSignal?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
