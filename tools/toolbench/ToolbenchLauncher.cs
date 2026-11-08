// ToolbenchLauncher: runs the stock Gamebryo 3.2 ToolbenchWindowed.exe (built for CLR2/.NET 3.5) on CLR4 (.NET 4.8 under Wine).
// Why: since .NET 4, AppDomain.AssemblyResolve is also raised for satellite resource assemblies. Toolbench's
// PluginActivator.OnAssemblyResolve logs Properties.Resources.UnableToFind, whose lookup probes the satellite for the current
// UI culture again -> infinite recursion -> StackOverflowException at startup. Forcing the invariant UI culture (Toolbench's own
// settings.xml default is "Invariant" anyway) makes ResourceManager use the neutral resources without probing satellites.
// Build (inside the Toolbench Wine prefix): csc /platform:x86 /target:winexe /out:ToolbenchLauncher.exe ToolbenchLauncher.cs
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

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

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        Assembly tb = Assembly.LoadFrom(Path.Combine(dir, "ToolbenchWindowed.exe"));
        MethodInfo main = tb.EntryPoint;
        object r = main.Invoke(null, new object[] { args });
        return r is int ? (int)r : 0;
    }
}
