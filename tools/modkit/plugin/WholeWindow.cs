// "Use the whole window": when an MS2 Mods view is opened, Toolbench's Game Solution pane (left) and Output View pane
// (bottom) go to auto-hide, collapsed to their edge tab, so the view gets the full width and height. Once per Toolbench
// session, and only for those two panes when they are docked and showing at that moment: a pane the user pins open again
// stays open. The menu item MS2 Mods > Use the whole window turns it off; the choice is the PerUser setting
// MS2.Mods.UseWholeWindow, kept by Toolbench with the plugin's other settings.
using System;
using System.Windows.Forms;
using Emergent.Toolbench;
using Emergent.Toolbench.Settings;
using Emergent.Toolbench.UI.Workbench;
using WeifenLuo.WinFormsUI.Docking;

namespace MS2.Tables
{
    internal static class WholeWindow
    {
        public const string SettingId = "MS2.Mods.UseWholeWindow";
        static readonly string[] Panes = { "Game Solution", "Output View" };
        static bool done;

        public static bool Enabled
        {
            get
            {
                bool v;
                ISettingService s = Platform.GetService<ISettingService>();
                return s == null || !s.GetSetting<bool>(SettingId, out v) || v;
            }
            set
            {
                ISettingService s = Platform.GetService<ISettingService>();
                if (s == null) return;
                s.SetSetting(SettingId, value);
                try { s.SaveSettings(SettingStorageMode.PerUser); }
                catch (Exception ex) { Logger.Warning("MS2 Mods: could not save the settings: " + ex.Message); }
            }
        }

        /// <summary>Collapses the two panes the first time it is called in a session with the setting on.</summary>
        public static void Collapse(DockPanel p)
        {
            if (done || p == null || !Enabled) return;
            done = true;
            try
            {
                foreach (IDockContent content in p.Contents)
                {
                    var dc = content as DockContent;
                    if (dc == null || dc.IsHidden || Array.IndexOf(Panes, dc.Text) < 0) continue;
                    DockState to = AutoHide(dc.DockState);
                    if (to != DockState.Unknown) dc.DockState = to;
                }
            }
            catch (Exception ex) { Logger.Warning("MS2 Mods: could not collapse the side panes: " + ex.Message); }
        }

        static DockState AutoHide(DockState s)
        {
            switch (s)
            {
                case DockState.DockLeft: return DockState.DockLeftAutoHide;
                case DockState.DockRight: return DockState.DockRightAutoHide;
                case DockState.DockTop: return DockState.DockTopAutoHide;
                case DockState.DockBottom: return DockState.DockBottomAutoHide;
                default: return DockState.Unknown;
            }
        }
    }

    /// <summary>The checked menu item MS2 Mods > Use the whole window.</summary>
    public class WholeWindowMenu : IMenuFactory
    {
        public ToolStripItem CreateMenu()
        {
            var item = new ToolStripMenuItem("Use the &whole window");
            item.ToolTipText = "When a view opens, collapse the Game Solution and Output View panes to their edge tabs (once per session)";
            item.CheckOnClick = true;
            item.Checked = WholeWindow.Enabled;
            item.OwnerChanged += delegate
            {
                var owner = item.Owner as ToolStripDropDown;
                if (owner != null) owner.Opening += delegate { item.Checked = WholeWindow.Enabled; };
            };
            item.CheckedChanged += delegate { if (item.Checked != WholeWindow.Enabled) WholeWindow.Enabled = item.Checked; };
            return item;
        }
    }
}
