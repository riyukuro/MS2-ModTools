// MS2.Tables: Gamebryo Toolbench plugin with the table editor of the mod kit. Usage: tools/modkit/README.md.
//
// This file: the plugin class, the six folder settings and the menu actions. TablesView.cs, ItemsView.cs, NpcsView.cs and GroupsView.cs
// are the views. All the work (reading archives, the mod.xml model, plan, apply, revert) is in MS2.ModKit.dll, which sits beside this DLL.
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

    /// <summary>The folders and the command after an apply. A setting that is empty falls back to its environment variable.</summary>
    internal sealed class TablesSettings
    {
        public string ClientData, GmsData, ServerData, ModsDir, Workspace, GameDir, ServerTarget, AfterApply;

        public static TablesSettings Load()
        {
            ISettingService s = Platform.GetService<ISettingService>();
            var t = new TablesSettings();
            t.ClientData = Get(s, "MS2.Mods.ClientData", "MS2_CLIENT_DATA");
            t.GmsData = Get(s, "MS2.Mods.GmsData", "MS2_GMS_CLIENT_DATA");
            t.ServerData = Get(s, "MS2.Mods.ServerData", "MS2_SERVER_DATA");
            t.ModsDir = Get(s, "MS2.Mods.ModsDir", "MS2_MODS");
            t.Workspace = Get(s, "MS2.Mods.Workspace", "MS2_MOD_WORKSPACE");
            t.GameDir = Get(s, "MS2.Mods.GameDir", "MS2_GAME_DIR");
            t.ServerTarget = Get(s, "MS2.Mods.ServerTarget", "MS2_SERVER_TARGET");
            t.AfterApply = Get(s, "MS2.Mods.AfterApply", "MS2_AFTER_APPLY");
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

    /// <summary>Shows one of the entity views (MS2 Items, MS2 NPCs). The Tables view is made first: the session and the mod are its.</summary>
    public abstract class ShowEntityViewAction : IActionHandler
    {
        protected abstract string ViewId { get; }
        protected abstract string Title { get; }

        public ExecutionResult Execute(ExecutionContext context, ExecutionStyle style)
        {
            try
            {
                IViewService views = Platform.GetService<IViewService>();
                views.CreateOrFindView(TablesView.ViewId);
                AbstractView view = views.CreateOrFindView(ViewId) as AbstractView;
                if (view == null)
                {
                    Logger.Error(Title + ": the view " + ViewId + " could not be created");
                    return ExecutionResult.Failed;
                }
                views.ShowView(view);
                return ExecutionResult.Success;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, Title);
                return ExecutionResult.Failed;
            }
        }
    }

    public class ShowItemsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return ItemsView.ViewId; } }
        protected override string Title { get { return "MS2 Items"; } }
    }

    public class ShowNpcsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return NpcsView.ViewId; } }
        protected override string Title { get { return "MS2 NPCs"; } }
    }

    public class ShowGroupsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return GroupsView.ViewId; } }
        protected override string Title { get { return "MS2 Shops and Drops"; } }
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
