// ToolbenchLauncher: runs the stock Gamebryo 3.2 ToolbenchWindowed.exe (built for CLR2/.NET 3.5) on CLR4 (.NET 4.8 under Wine).
// Why: since .NET 4, AppDomain.AssemblyResolve is also raised for satellite resource assemblies. Toolbench's
// PluginActivator.OnAssemblyResolve logs Properties.Resources.UnableToFind, whose lookup probes the satellite for the current
// UI culture again -> infinite recursion -> StackOverflowException at startup. Forcing the invariant UI culture (Toolbench's own
// settings.xml default is "Invariant" anyway) makes ResourceManager use the neutral resources without probing satellites.
//
// Second fix, also a .NET 4 change: when a list drops an item, WPF 4 sets the DataContext of the item's controls to a
// "disconnected item" marker before it raises Unloaded (WPF 3.5 left the data there). The palette's item control casts its
// DataContext in its Unloaded handler without a check, so leaving any palette list (open a library, search, pick a folder)
// ended Toolbench with a NullReferenceException. That one exception is marked handled here; the cost is that the dropped item
// stays subscribed to its data object until the palette is closed.
// Build (inside the Toolbench Wine prefix):
//   csc /platform:x86 /target:winexe /r:WPF\WindowsBase.dll /out:ToolbenchLauncher.exe ToolbenchLauncher.cs
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;

static class ToolbenchLauncher
{
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = inv;
        CultureInfo.DefaultThreadCurrentCulture = inv;
        Thread.CurrentThread.CurrentUICulture = inv;
        Thread.CurrentThread.CurrentCulture = inv;

        // Toolbench builds its windows on this thread
        Dispatcher.CurrentDispatcher.UnhandledException += OnUiException;

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        Assembly tb = Assembly.LoadFrom(Path.Combine(dir, "ToolbenchWindowed.exe"));
        MethodInfo main = tb.EntryPoint;
        object r = main.Invoke(null, new object[] { args });
        return r is int ? (int)r : 0;
    }

    static bool s_said;

    static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MethodBase at = e.Exception is NullReferenceException ? e.Exception.TargetSite : null;
        if (at == null || at.Name != "OnUnloaded" || at.DeclaringType == null
            || at.DeclaringType.FullName != "Emergent.Toolbench.EntityModeling.UI.ProjectExplorer.EditItem") return;
        e.Handled = true;
        if (s_said) return;
        s_said = true;
        Console.Error.WriteLine("ToolbenchLauncher: passed over the palette item's unload fault (.NET 4, see ToolbenchLauncher.cs)");
    }
}
