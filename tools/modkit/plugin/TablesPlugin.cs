// MS2.Tables: Gamebryo Toolbench plugin with the table editor of the mod kit. Usage: tools/modkit/README.md.
//
// This file: the plugin class, the six folder settings and the menu actions. TablesView.cs, ItemsView.cs, NpcsView.cs, SkillsView.cs, GroupsView.cs, RecipesView.cs, ItemOptionsView.cs, ItemSetsView.cs, DungeonsView.cs, SpawnsView.cs, GlobalDropsView.cs, AchievementsView.cs, QuestsView.cs, DialogueView.cs, TriggersView.cs, TextImportView.cs, FieldRowsView.cs and
// MapsView.cs, SoundView.cs, LevelsView.cs, ConstantsView.cs and AttendanceView.cs
// are the views. All the work (reading archives, the mod.xml model, plan, apply, revert) is in MS2.ModKit.dll, which sits beside this DLL.
// Targets .NET 3.5 / C# 7.3 (Toolbench is a CLR 2 x86 host).
using System;
using System.Collections.Generic;
using Emergent.Toolbench;
using Emergent.Toolbench.Actions;
using Emergent.Toolbench.Settings;
using Emergent.Toolbench.UI.Workbench;
using MS2.ModKit;

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
        /// <summary>The schema cache folder ("" = no schema check) and the notes file that goes with it (null = none found).</summary>
        public string SchemaDir, SchemaNotes;
        /// <summary>Per data root: the features setting ("LOCALE/TYPE", "" = the profile's own) and where it comes from when not from the view's field.</summary>
        public readonly Dictionary<string, string> Features = new Dictionary<string, string>(), FeaturesFrom = new Dictionary<string, string>();

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
            // the features: the environment, then modtools.toml of the checkout this plugin was built in or runs in, then the view's field
            RepoSettings repo = RepoSettings.Load(System.IO.Path.GetDirectoryName(typeof(TablesSettings).Assembly.Location), System.IO.Directory.GetCurrentDirectory());
            t.SchemaDir = Get(s, "MS2.Mods.SchemaDir", "MS2_MOD_SCHEMA");
            t.SchemaNotes = NotesFile(System.IO.Path.GetDirectoryName(typeof(TablesSettings).Assembly.Location), repo.Repo);
            foreach (Profile p in Profile.All)
            {
                string from = repo.SourceOf(p.FeaturesVar), v = from != null ? repo.Value(p.FeaturesVar).Trim() : null;
                if (v == null && (s == null || !s.GetSetting<string>(FeaturesId(p), out v))) v = null;
                t.Features[p.Root] = (v ?? "").Trim();
                t.FeaturesFrom[p.Root] = from;
            }
            return t;
        }

        // schema-notes.xml: the copy next to the plugin, else the one of the checkout (the file the command line reads)
        static string NotesFile(string pluginDir, string repo)
        {
            string f = System.IO.Path.Combine(pluginDir ?? "", "schema-notes.xml");
            if (System.IO.File.Exists(f)) return f;
            if (repo == null) return null;
            f = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(repo, "tools"), "modkit"), "schema-notes.xml");
            return System.IO.File.Exists(f) ? f : null;
        }

        public static string FeaturesId(Profile p) { return "MS2.Mods.Features." + p.Name; }

        const string AllowConflictsId = "MS2.Mods.AllowConflicts";

        /// <summary>The view's "higher mod wins" box: apply although mods conflict.</summary>
        public static bool LoadAllowConflicts()
        {
            ISettingService s = Platform.GetService<ISettingService>();
            bool v;
            return s != null && s.GetSetting<bool>(AllowConflictsId, out v) && v;
        }

        public static void SaveAllowConflicts(bool on)
        {
            ISettingService s = Platform.GetService<ISettingService>();
            if (s == null) return;
            s.SetSetting(AllowConflictsId, on);
            try { s.SaveSettings(SettingStorageMode.PerUser); }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not save the settings: " + ex.Message); }
        }

        /// <summary>Saves the view's features field of a profile.</summary>
        public static void SaveFeatures(Profile p, string value)
        {
            ISettingService s = Platform.GetService<ISettingService>();
            if (s == null) return;
            s.SetSetting(FeaturesId(p), value ?? "");
            try { s.SaveSettings(SettingStorageMode.PerUser); }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not save the settings: " + ex.Message); }
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
                if (ShowsTables || !TablesView.ItemsInUse) { views.ShowView(view); ViewLayout.ToCentre(view); }
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
                TablesView.OpenBehind();
                AbstractView view = views.CreateOrFindView(ViewId) as AbstractView;
                if (view == null)
                {
                    Logger.Error(Title + ": the view " + ViewId + " could not be created");
                    return ExecutionResult.Failed;
                }
                views.ShowView(view);
                ViewLayout.ToCentre(view);
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

    public class ShowSkillsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return SkillsView.ViewId; } }
        protected override string Title { get { return "MS2 Skills"; } }
    }

    public class ShowGroupsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return GroupsView.ViewId; } }
        protected override string Title { get { return "MS2 Shops and Drops"; } }
    }

    public class ShowRecipesAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return RecipesView.ViewId; } }
        protected override string Title { get { return "MS2 Recipes"; } }
    }

    public class ShowItemOptionsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return ItemOptionsView.ViewId; } }
        protected override string Title { get { return "MS2 Item options"; } }
    }

    public class ShowItemSetsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return ItemSetsView.ViewId; } }
        protected override string Title { get { return "MS2 Item sets"; } }
    }

    public class ShowDungeonsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return DungeonsView.ViewId; } }
        protected override string Title { get { return "MS2 Dungeons"; } }
    }

    public class ShowSpawnsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return SpawnsView.ViewId; } }
        protected override string Title { get { return "MS2 Spawns"; } }
    }

    public class ShowGlobalDropsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return GlobalDropsView.ViewId; } }
        protected override string Title { get { return "MS2 Global drops"; } }
    }

    public class ShowAchievementsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return AchievementsView.ViewId; } }
        protected override string Title { get { return "MS2 Achievements"; } }
    }

    public class ShowSoundAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return SoundView.ViewId; } }
        protected override string Title { get { return "MS2 Sound"; } }
    }

    public class ShowLevelsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return LevelsView.ViewId; } }
        protected override string Title { get { return "MS2 Levels"; } }
    }

    public class ShowConstantsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return ConstantsView.ViewId; } }
        protected override string Title { get { return "MS2 Constants"; } }
    }

    public class ShowAttendanceAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return AttendanceView.ViewId; } }
        protected override string Title { get { return "MS2 Attendance"; } }
    }

    public class ShowDialogueAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return DialogueView.ViewId; } }
        protected override string Title { get { return "MS2 Dialogue"; } }
    }

    public class ShowQuestsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return QuestsView.ViewId; } }
        protected override string Title { get { return "MS2 Quests"; } }
    }

    public class ShowTriggersAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return TriggersView.ViewId; } }
        protected override string Title { get { return "MS2 Triggers"; } }
    }

    public class ShowTextImportAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return TextImportView.ViewId; } }
        protected override string Title { get { return "MS2 Text import"; } }
    }

    public class ShowFieldRowsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return FieldRowsView.ViewId; } }
        protected override string Title { get { return "MS2 Field rows"; } }
    }

    public class ShowMapsAction : ShowEntityViewAction
    {
        protected override string ViewId { get { return MapsView.ViewId; } }
        protected override string Title { get { return "MS2 Maps"; } }
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
