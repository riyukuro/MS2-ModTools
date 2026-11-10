// MS2.Tables: Gamebryo Toolbench plugin with the table editor of the mod kit. Usage: tools/modkit/README.md.
//
// This file: the plugin class, the four folder settings and the menu actions. TablesView.cs is the view. All the
// work (reading archives, the mod.xml model, plan, apply, revert) is in MS2.ModKit.dll, which sits beside this DLL.
// Targets .NET 3.5 / C# 7.3 (Toolbench is a CLR 2 x86 host).
using System;
using Emergent.Toolbench;
using Emergent.Toolbench.Actions;
using Emergent.Toolbench.Settings;
using Emergent.Toolbench.UI.Workbench;

namespace MS2.Tables
{
    internal class Plugin : PluginBase
    {
        public override void Start() { }
        public override void Stop() { }
    }

    /// <summary>The four folders. A setting that is empty falls back to its environment variable.</summary>
    internal sealed class TablesSettings
    {
        public string ClientData, ServerData, ModsDir, Workspace;

        public static TablesSettings Load()
        {
            ISettingService s = Platform.GetService<ISettingService>();
            var t = new TablesSettings();
            t.ClientData = Get(s, "MS2.Mods.ClientData", "MS2_CLIENT_DATA");
            t.ServerData = Get(s, "MS2.Mods.ServerData", "MS2_SERVER_DATA");
            t.ModsDir = Get(s, "MS2.Mods.ModsDir", "MS2_MODS");
            t.Workspace = Get(s, "MS2.Mods.Workspace", "MS2_MOD_WORKSPACE");
            return t;
        }

        static string Get(ISettingService s, string id, string env)
        {
            string v = null;
            if (s != null && !s.GetSetting<string>(id, out v)) v = null;
            v = (v ?? "").Trim().Trim('"');
            if (v.Length == 0) v = (Environment.GetEnvironmentVariable(env) ?? "").Trim();
            return Environment.ExpandEnvironmentVariables(v);
        }
    }

    public abstract class TablesAction : IActionHandler
    {
        public ExecutionResult Execute(ExecutionContext context, ExecutionStyle style)
        {
            try
            {
                IViewService views = Platform.GetService<IViewService>();
                TablesView view = views.CreateOrFindView(TablesView.ViewId) as TablesView;
                if (view == null)
                {
                    Logger.Error("MS2 Tables: the view " + TablesView.ViewId + " could not be created");
                    return ExecutionResult.Failed;
                }
                views.ShowView(view);
                return Run(view) ? ExecutionResult.Success : ExecutionResult.Failed;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MS2 Tables");
                return ExecutionResult.Failed;
            }
        }

        protected abstract bool Run(TablesView view);
    }

    public class ShowAction : TablesAction { protected override bool Run(TablesView view) { return true; } }
    public class PlanAction : TablesAction { protected override bool Run(TablesView view) { return view.DoPlan(); } }
    public class ApplyAction : TablesAction { protected override bool Run(TablesView view) { return view.DoApply(); } }
    public class RevertAction : TablesAction { protected override bool Run(TablesView view) { return view.DoRevert(); } }
}
