// MS2.Tables: Gamebryo Toolbench plugin with the table editor of the mod kit. Usage: tools/modkit/README.md.
//
// This file: the plugin class, the five folder settings and the menu actions. TablesView.cs and ItemsView.cs are
// the views. All the
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

    /// <summary>The five folders. A setting that is empty falls back to its environment variable.</summary>
    internal sealed class TablesSettings
    {
        public string ClientData, ServerData, ModsDir, Workspace, GameDir;

        public static TablesSettings Load()
        {
            ISettingService s = Platform.GetService<ISettingService>();
            var t = new TablesSettings();
            t.ClientData = Get(s, "MS2.Mods.ClientData", "MS2_CLIENT_DATA");
            t.ServerData = Get(s, "MS2.Mods.ServerData", "MS2_SERVER_DATA");
            t.ModsDir = Get(s, "MS2.Mods.ModsDir", "MS2_MODS");
            t.Workspace = Get(s, "MS2.Mods.Workspace", "MS2_MOD_WORKSPACE");
            t.GameDir = Get(s, "MS2.Mods.GameDir", "MS2_GAME_DIR");
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

    /// <summary>Asked by Toolbench before it exits; false keeps it open.</summary>
    public class ClosingHandler : IWorkbenchClosingHandler
    {
        public bool Execute()
        {
            TablesView view = TablesView.Open;
            return view == null || view.ConfirmShutdown();
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
                // from the Items view the command runs where it is: its result shows in that view's own output box
                if (ShowsTables || !TablesView.ItemsInUse) views.ShowView(view);
                else view.NeedHandle();
                return Run(view) ? ExecutionResult.Success : ExecutionResult.Failed;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MS2 Tables");
                return ExecutionResult.Failed;
            }
        }

        protected abstract bool Run(TablesView view);
        /// <summary>True for the command that is there to show the Tables view.</summary>
        protected virtual bool ShowsTables { get { return false; } }
    }

    /// <summary>Shows the MS2 Items view. The Tables view is made first: the session and the mod are its.</summary>
    public class ShowItemsAction : IActionHandler
    {
        public ExecutionResult Execute(ExecutionContext context, ExecutionStyle style)
        {
            try
            {
                IViewService views = Platform.GetService<IViewService>();
                views.CreateOrFindView(TablesView.ViewId);
                AbstractView view = views.CreateOrFindView(ItemsView.ViewId) as AbstractView;
                if (view == null)
                {
                    Logger.Error("MS2 Items: the view " + ItemsView.ViewId + " could not be created");
                    return ExecutionResult.Failed;
                }
                views.ShowView(view);
                return ExecutionResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MS2 Items");
                return ExecutionResult.Failed;
            }
        }
    }

    public class ShowAction : TablesAction
    {
        protected override bool Run(TablesView view) { return true; }
        protected override bool ShowsTables { get { return true; } }
    }
    public class PlanAction : TablesAction { protected override bool Run(TablesView view) { return view.DoPlan(); } }
    public class ApplyAction : TablesAction { protected override bool Run(TablesView view) { return view.DoApply(); } }
    public class RevertAction : TablesAction { protected override bool Run(TablesView view) { return view.DoRevert(); } }
}
