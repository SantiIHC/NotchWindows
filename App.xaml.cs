using System.Windows;

namespace IslaDinamica;

public partial class App : Application
{
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Evita abrir dos islas a la vez
        _mutex = new Mutex(true, "IslaDinamica_SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
