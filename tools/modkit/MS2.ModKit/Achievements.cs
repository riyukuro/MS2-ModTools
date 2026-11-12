// Achievements (trophies): achieve/<id>.xml in Xml.m2d of every side, one <achieves> per file with its grades. Each
// grade has one condition (what the player does, as quest conditions are written) and at most one reward.
//
// How the sides read the files:
//   - kms client (CAchieveData::LoadAll 0x14015c050, Load 0x14015c270): reads the id attribute, not the file name;
//     type = id / 10000000 (2 trophy, 8 and 9 emblems; an id below 10000000 is skipped), category = the second digit
//     (1 combat, 2 adventure, 3 living: the tally at 0x1409388a0 and the name tags 0x76e/0x76d/0x76f). A condition
//     whose type it does not know (ConditionTypeFromName 0x14029f210, via CConditionCommonData::Load 0x14029ea20)
//     drops the achievement and every later one of the file; a grade value twice logs a "grade {0} of the trophy is there twice" line
//     and the later grade wins; a first grade other than 1 (not for type 9) logs a "the lowest grade is not 1" line. Reward
//     types are ParseRewardType 0x14015dd20. The list tabs filter on categoryTag (CAchieveCategory::Load 0x14093e610
//     reads table/achievecategory.xml).
//   - server (Maple2.File.Parser AchieveParser, ingest AchievementMapper): every achieve/ entry of Xml.m2d; the id is
//     the file name, the name comes from string/en/achievename.xml. Empty attributes are dropped first; a condition
//     or reward type that is not a name it knows throws (Enum.Parse), so does a grade value twice (a dictionary).
//   - the kms client's files are written short (no empty attributes, no empty reward); the server's and the global
//     client's are written in full (code="" target="", reward type="" code="0" value="0" rank="1"). Each side keeps
//     its own notation here.
// New achievements take ids 2C900000-2C909999 (C = the category digit 1-3): no shipped side uses a third digit 9.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public static class AchieveRules
    {
        public const string Space = "achieve", Folder = "achieve/", Element = "achieves";
        public const string NameFile = "achievename.xml", DescFile = "achievedescription.xml";
        public const string CategoryEntry = "table/achievecategory.xml", TitleEntry = "table/titletag.xml";
        public const long NewBase = 900000, NewSpan = 9999;

        static readonly Regex EntryRx = new Regex(@"^achieve/(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsEntry(string entry) { return entry != null && EntryRx.IsMatch(entry.Replace('\\', '/')); }

        /// <summary>The id of an achieve/&lt;id&gt;.xml entry, or 0.</summary>
        public static long IdOf(string entry)
        {
            if (entry == null) return 0;
            Match m = EntryRx.Match(entry.Replace('\\', '/'));
            long id;
            return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) ? id : 0;
        }

        public static string EntryOf(long id) { return Folder + id.ToString(CultureInfo.InvariantCulture) + ".xml"; }

        /// <summary>2 trophy, 8 and 9 emblems (the client's m_nType).</summary>
        public static int TypeOf(long id) { return (int)(id / 10000000); }

        /// <summary>The second digit: for a trophy 1 combat, 2 adventure, 3 living.</summary>
        public static int CategoryOf(long id) { return (int)(id / 1000000 % 10); }

        public static readonly string[] Categories = { null, "combat", "adventure", "living" };

        /// <summary>The first id of the range for new trophies of a category (1-3).</summary>
        public static long NewFrom(int category) { return 20000000 + category * 1000000L + NewBase; }
        public static long NewTo(int category) { return NewFrom(category) + NewSpan; }

        public static bool InNewRange(long id)
        {
            int c = CategoryOf(id);
            return TypeOf(id) == 2 && c >= 1 && c <= 3 && id >= NewFrom(c) && id <= NewTo(c);
        }

        /// <summary>The reward types both sides read (the server reads "etc" as dynamicaction).</summary>
        public static readonly string[] RewardTypes =
        {
            "item", "title", "statPoint", "skillPoint", "shop_weapon", "shop_build", "shop_ride", "itemcoloring",
            "beauty_makeup", "beauty_skin", "beauty_hair", "dynamicaction", "etc",
        };

        /// <summary>The condition types the server reads (its ConditionType names without unknown and level_max), sorted.</summary>
        public static readonly string[] ConditionTypes =
        {
            "adventure_level", "adventure_level_up", "attendance", "auto_fishing", "banner", "beauty_add", "beauty_change", "beauty_change_color", 
            "beauty_random", "beauty_style_add", "beauty_style_apply", "best_taxi_fee", "best_taxi_use", "breakable_object", "buddy_request", "buff", 
            "buy_cube", "buy_house", "change_equip", "change_profile", "change_ugc_equip", "character_ability_learn", "character_ability_reset", "chat", 
            "climb", "club_join", "commend_home", "continent", "controller", "controller_mine", "controller_other", "couple_dance_event", 
            "crawl", "create_blueprint", "dialogue", "donation_item", "donation_type", "dungeon_clear", "dungeon_clear_group", "dungeon_first_clear", 
            "dungeon_help_beginner", "dungeon_help_beginner_helpee", "dungeon_help_beginner_helper", "dungeon_key_use", "dungeon_random_bonus", "dungeon_rank", "dungeon_rank_clear", "dungeon_rank_clear_group", 
            "dungeon_reward", "dungeon_reward_group", "dungeon_round_clear", "emotion", "emotiontime", "empty", "enchant_result", "enter_otherhouse", 
            "equip_exist", "equip_gemstone_putoff", "equip_gemstone_puton", "exp", "explore", "explore_continent", "exp_rate", "extend_house", 
            "fall", "fall_damage", "fall_die", "fall_survive", "fame_grade", "fame_point", "festival_event", "field_mission", 
            "fish", "fish_big", "fish_collect", "fisher_grade", "fish_fail", "fish_goldmedal", "fish_success_bait", "game_helper_service", 
            "gemstone_putoff", "gemstone_puton", "gemstone_upgrade", "gemstone_upgrade_fail", "gemstone_upgrade_success", "gemstone_upgrade_try", "get_habi_token", "get_honor_token", 
            "get_karma_token", "get_lu_token", "get_mentee_token", "get_mentor_token", "get_reverse_coin", "get_star_point", "glide", "guild_attendance", 
            "guild_championship", "guild_donate", "guild_exp", "guild_join", "guild_join_req", "guildpvp_die", "guildpvp_kill", "guildpvp_win", 
            "guild_trigger", "guild_trophy", "hero_achieve", "hero_achieve_grade", "hit_tombstone", "holdtime", "home_bank", "home_doctor", 
            "home_goto", "idip_adventure_bar", "idip_app_attendance", "idip_live_broadcast", "install_billboard", "install_item", "interact_npc", "interact_object", 
            "interact_object_rep", "interior_exp", "interior_exp_offset", "interior_level", "interior_point", "item_add", "item_break", "item_collect", 
            "item_collect_revise", "item_design", "item_destroy", "item_exist", "item_gear_score", "item_inven", "item_merge_success", "item_move", 
            "item_pickup", "item_remake_option", "item_remake_option_record", "job", "job_change", "jump", "killcount", "laddertime", 
            "lapenshard_upgrade_fail", "lapenshard_upgrade_result", "lapenshard_upgrade_success", "lapenshard_upgrade_try", "level", "level_up", "limited_bundle_buy", "maid_affinity", 
            "maid_get_item", "maid_jackpot", "maid_profile", "maid_salary", "map", "mastery_farming", "mastery_farming_try", "mastery_gathering", 
            "mastery_gathering_try", "mastery_grade", "mastery_harvest", "mastery_harvest_guildhouse", "mastery_harvest_otherhouse", "mastery_harvest_try", "mastery_manufacturing", "meso", 
            "meso_donation", "minigame_clear", "mission_attack_system", "mission_point", "music_concert_cheer_up", "music_play_ensemble", "music_play_ensemble_in", "music_play_grade", 
            "music_play_instrument_mastery", "music_play_instrument_time", "music_play_score", "music_play_score_by_name", "music_play_score_time", "npc", "npc_assist_bonus", "npc_dungeon_boss", 
            "npc_event_tag", "npc_field_boss", "npc_field_elite", "npc_lasthit", "npc_lasthit_buff", "npc_lasthit_time", "npc_no_damage", "npc_race", 
            "npc_timeattack", "nurturing_eat", "nurturing_growth", "nurturing_play", "pet_catch_category", "pet_catch_grade", "pet_catch_id", "pet_collect", 
            "pet_enchant", "pet_enchant_exp", "pet_evolution_by_rank", "pet_evolution_point_by_rank", "pet_first_collect", "pet_remake_option", "pet_remake_option_record", "pet_taming", 
            "play_ensenble_time", "playinstrument_time", "play_rps", "play_rps_draw", "play_rps_lose", "play_rps_win", "playtime", "pvp_die", 
            "pvp_kill", "pvp_participation", "pvp_win", "pvp_win_perfect", "pvp_win_score", "pvp_win_time", "pvp_win_with_buff", "pvp_win_with_grade", 
            "quest", "quest_accept", "quest_alliance", "quest_alliance_by_grade", "quest_clear", "quest_clear_by_chapter", "quest_daily", "quest_field", 
            "quest_field_first_clear", "quest_guide", "quest_revise_achieve", "repeat_quest_clear", "resolve_panelty", "revise_achieve_multi_grade", "revise_achieve_single_grade", "revival", 
            "riding", "ropetime", "rotate_cube", "run", "send_mail", "set_mastery_grade", "shadow_world_die", "shadow_world_kill", 
            "shop_buy", "shop_buy_habi_token", "shop_buy_honor_token", "shop_buy_karma_token", "shop_buy_lu_token", "shop_buy_mentee_token", "shop_buy_mentro_token", "shop_buy_reverse_coin", 
            "shop_buy_star_point", "shop_sell", "skill", "skill_damage_npc", "skill_die", "skin_gemstone_putoff", "skin_gemstone_puton", "skyfortress_system", 
            "smart_push", "socket_unlock", "socket_unlock_fail", "socket_unlock_success", "socket_unlock_try", "spawner", "splash_plant", "stay_cube", 
            "stay_map", "subjob_change", "survival_breakable_object", "survival_buy_gold_pass", "survival_double_kill", "survival_enter", "survival_item_get", "survival_kill", 
            "survival_kill_outside", "survival_kill_use_skill", "survival_npc_kill", "survival_rank_with_kill", "survival_total_kill_use_single_skill", "survival_total_kill_use_skill", "survival_win_use_one_skill", "survival_win_without_interact", 
            "survival_win_without_npckill", "survive_cube", "survive_map", "swim", "swimtime", "talk_in", "taxifee", "taxifind", 
            "taxiuse", "trigger", "trophy_point", "uninstall_item", "unlimited_enchant", "use_merat", "user_find", "useropen_minigame_clear", 
            "vibrate_object", "vipgm", "wedding_complete", "wedding_divorce", "wedding_guest", "wedding_hall_cancel", "wedding_hall_change", "wedding_hall_reserve", 
            "wedding_propose", "wedding_propose_decline", "wedding_propose_declined", "worldchampion_damage", "worldchampion_hit", "worldchampion_reward", 
        };

        /// <summary>The condition types the kms client reads and the server does not (conditionutil.cpp AddCondition).</summary>
        static readonly string[] ClientOnlyConditions = { "item_collect_script_function", "openItemBox", "openStoryBook" };

        /// <summary>The keys a user_find condition's target dictionary may have: the client's
        /// CQuestConditionCheckerUserFind handlers (conditionutil.cpp, its constructor), compared case-insensitively.</summary>
        public static readonly string[] UserFindKeys =
        {
            "jobId", "level", "gender", "equipItemIds", "equipItemIdsAnd", "equipItemIdsOr", "titleId", "rideeId", "oppositeGender",
        };

        /// <summary>Is the condition type one the side reads? The kms client knows ClientOnlyConditions too.</summary>
        public static bool KnownCondition(string type, bool server)
        {
            if (string.IsNullOrEmpty(type)) return false;
            if (!server) foreach (string t in ClientOnlyConditions) if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string t in ConditionTypes) if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The client's CConditionCommonData::Validate (conditionutil.cpp): a user_find condition's target must be a
        /// dictionary, "dict:{key:value,...}", whose keys are UserFindKeys; when it is not the client logs it and drops
        /// the achievement and the rest of the file. Null when the condition passes; other types always pass.
        /// </summary>
        public static string ValidateCondition(string type, string target)
        {
            if (!string.Equals(type, "user_find", StringComparison.OrdinalIgnoreCase)) return null;
            string t = target ?? "";
            // the client parses the target only when it is set and not "0"; an unparsed target has no dictionary
            if (t.Length == 0 || t == "0" || !t.StartsWith("dict:", StringComparison.Ordinal))
                return "the user_find condition's target is not a dictionary (dict:{key:value,...})";
            string body = t.Substring(5).Trim('{', '}', ' ');
            foreach (string part in body.Split(','))
            {
                if (part.Length == 0) continue;
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;   // the client skips a part without a key
                string key = part.Substring(0, colon).Trim();
                bool known = false;
                foreach (string k in UserFindKeys) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                if (!known) return "the user_find condition's target key \"" + key + "\" is not one the client checks (" + string.Join(", ", UserFindKeys) + ")";
            }
            return null;
        }

        public static bool KnownReward(string type)
        {
            foreach (string t in RewardTypes) if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The fields of an achievement the editor shows, in the editor's notation ("" = not set or the default).</summary>
        public static readonly string[] Fields =
        {
            "icon", "categoryTag", "feature", "locale", "account", "locking", "noticePercent", "standardLevel", "contentsTag", "uiFilterType", "uiFilterValue",
        };

        /// <summary>The grade columns: the grade value, its condition and its reward.</summary>
        public static readonly string[] GradeFields =
        {
            "value", "condition@type", "condition@code", "condition@value", "condition@target", "reward@type", "reward@code", "reward@value", "reward@rank",
        };

        // the attributes of <achieves> that the full notation always writes, in its order, with their empty value
        static readonly string[] FullAttrs = { "id", "account", "icon", "noticePercent", "locking", "categoryTag", "feature" };
        static readonly string[] FullEmpty = { "", "0", "", "1", "", "", "" };
        static readonly string[] ShortOrder = { "id", "feature", "categoryTag", "icon", "account", "locking" };

        /// <summary>The value the field has when it is not written ("" for none).</summary>
        public static string DefaultOf(string field)
        {
            switch (field)
            {
                case "account": case "locking": case "standardLevel": case "reward@code": case "reward@value": return "0";
                case "noticePercent": case "reward@rank": return "1";
                default: return "";
            }
        }

        /// <summary>A stored value in the editor's notation: the default is "".</summary>
        public static string Neutral(string field, string stored)
        {
            if (stored == null) return "";
            stored = stored.Trim();
            return stored == DefaultOf(field) ? "" : stored;
        }

        static string ElemOf(string field, out string attr)
        {
            int at = field.IndexOf('@');
            attr = at < 0 ? field : field.Substring(at + 1);
            return at < 0 ? null : field.Substring(0, at);
        }

        internal static XmlElem Child(XmlElem e, string name)
        {
            foreach (XmlElem c in e.Children) if (c.Name == name) return c;
            return null;
        }

        internal static List<XmlElem> Grades(XmlElem achieves)
        {
            var l = new List<XmlElem>();
            foreach (XmlElem c in achieves.Children) if (c.Name == "grade") l.Add(c);
            return l;
        }

        /// <summary>A grade in the editor's notation, one cell per GradeFields column. A reward without a type is none.</summary>
        public static string[] GradeCells(XmlElem grade)
        {
            var cells = new string[GradeFields.Length];
            XmlElem reward = Child(grade, "reward");
            bool noReward = reward == null || string.IsNullOrEmpty((reward.Attr("type") ?? "").Trim());
            for (int i = 0; i < GradeFields.Length; i++)
            {
                string attr, el = ElemOf(GradeFields[i], out attr);
                XmlElem e = el == null ? grade : el == "reward" ? (noReward ? null : reward) : Child(grade, el);
                cells[i] = e == null ? "" : Neutral(GradeFields[i], e.Attr(attr));
            }
            return cells;
        }

        static void Attr(StringBuilder sb, string name, string value) { sb.Append(' ').Append(name).Append("=\"").Append(XmlDoc.EscapeAttr(value, '"')).Append('"'); }

        /// <summary>The text of a grade from its cells, in the full notation or the short one; inner lines indented one tab.</summary>
        public static string GradeText(string[] cells, bool full, string newLine)
        {
            var sb = new StringBuilder("<grade");
            Attr(sb, "value", cells[0]);
            sb.Append('>').Append(newLine).Append("\t<condition");
            for (int i = 1; i <= 4; i++)
            {
                string attr; ElemOf(GradeFields[i], out attr);
                if (full || cells[i].Length > 0) Attr(sb, attr, cells[i]);
            }
            sb.Append(" />").Append(newLine);
            if (full || cells[5].Length > 0)
            {
                sb.Append("\t<reward");
                for (int i = 5; i <= 8; i++)
                {
                    string attr; ElemOf(GradeFields[i], out attr);
                    string v = cells[5].Length == 0 && i > 5 ? "" : cells[i];
                    if (full) Attr(sb, attr, v.Length > 0 ? v : DefaultOf(GradeFields[i]));
                    else if (v.Length > 0) Attr(sb, attr, v);
                }
                sb.Append(" />").Append(newLine);
            }
            return sb.Append("</grade>").ToString();
        }

        /// <summary>A whole achievement file from an &lt;achieves&gt; element of any side, written in the given notation with the id.</summary>
        public static string FileText(XmlElem achieves, long id, bool full, string nl)
        {
            var attrs = new List<KeyValuePair<string, string>>();
            foreach (XmlAttr a in achieves.Attrs)
                if (a.Name != "id") attrs.Add(new KeyValuePair<string, string>(a.Name, Neutral(a.Name, a.Value)));
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + nl + "<ms2>" + nl + "\t<" + Element);
            Attr(sb, "id", id.ToString(CultureInfo.InvariantCulture));
            var done = new HashSet<string> { "id" };
            string[] order = full ? FullAttrs : ShortOrder;
            for (int i = 1; i < order.Length; i++)
            {
                done.Add(order[i]);
                string v = "";
                foreach (KeyValuePair<string, string> kv in attrs) if (kv.Key == order[i]) v = kv.Value;
                if (full) Attr(sb, order[i], v.Length > 0 ? v : FullEmpty[i]);
                else if (v.Length > 0) Attr(sb, order[i], v);
            }
            foreach (KeyValuePair<string, string> kv in attrs)
                if (done.Add(kv.Key) && kv.Value.Length > 0) Attr(sb, kv.Key, kv.Value);
            sb.Append('>').Append(nl);
            foreach (XmlElem g in Grades(achieves))
                sb.Append("\t\t").Append(GradeText(GradeCells(g), full, nl).Replace(nl + "\t", nl + "\t\t\t").Replace(nl + "</grade>", nl + "\t\t</grade>")).Append(nl);
            return sb.Append("\t</" + Element + ">").Append(nl).Append("</ms2>").ToString();
        }

        /// <summary>
        /// What is wrong with one side's file. errors: what makes the side fail or drop the achievement (the server's
        /// ingest throws; the client drops it); warnings: the rest. tags: the categoryTag values of
        /// table/achievecategory.xml (null: not checked).
        /// </summary>
        public static void CheckFile(XmlDoc doc, long fileId, bool server, ICollection<string> tags, List<string> errors, List<string> warnings)
        {
            var all = new List<XmlElem>();
            if (doc != null && doc.Root != null) foreach (XmlElem e in doc.Root.Children) if (e.Name == Element) all.Add(e);
            if (all.Count == 0) { errors.Add("the file has no <" + Element + ">"); return; }
            if (all.Count > 1) warnings.Add("the file has " + all.Count + " <" + Element + "> (feature or locale variants); the editor shows the first");
            XmlElem a = all[0];
            long id;
            string idText = a.Attr("id");
            if (!long.TryParse(idText ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out id)) errors.Add("id \"" + idText + "\" is not a number");
            else if (id != fileId) warnings.Add("id " + id + " is not the file's id " + fileId + ": the client reads the attribute, the server the file name");
            if (fileId < 10000000) warnings.Add("the id is below 10000000: the client skips it");
            int type = TypeOf(fileId), cat = CategoryOf(fileId);
            string tag = (a.Attr("categoryTag") ?? "").Trim();
            if (tags != null && tag.Length > 0 && type == 2 && !tags.Contains(tag))
                warnings.Add("categoryTag \"" + tag + "\" is not a category of " + CategoryEntry + ": no list shows it");
            if (type == 2 && cat >= 1 && cat <= 3 && tag.Length > 0)
            {
                string main = tag.Split(',')[0].Trim();
                if (!string.Equals(main, Categories[cat], StringComparison.OrdinalIgnoreCase))
                    warnings.Add("categoryTag \"" + tag + "\" is not of the category " + Categories[cat] + " that the id's second digit " + cat + " gives (the client counts it there)");
            }
            List<XmlElem> grades = Grades(a);
            if (grades.Count == 0) { errors.Add("there is no grade"); return; }
            var seen = new HashSet<long>();
            long last = long.MinValue, first = long.MaxValue;
            bool rising = true;
            for (int i = 0; i < grades.Count; i++)
            {
                XmlElem g = grades[i];
                string at = "grade " + (g.Attr("value") ?? "?");
                long v;
                if (!long.TryParse(g.Attr("value") ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v))
                {
                    errors.Add("grade " + (i + 1) + ": value \"" + g.Attr("value") + "\" is not a number");
                    continue;
                }
                if (!seen.Add(v)) (server ? errors : warnings).Add(at + " is there twice" + (server ? ": the server's data ingest fails" : ": the client keeps the later one"));
                else if (v <= last) rising = false;
                if (v < first) first = v;
                last = Math.Max(last, v);
                XmlElem c = Child(g, "condition");
                if (c == null) errors.Add(at + " has no condition");
                else
                {
                    string ct = (c.Attr("type") ?? "").Trim();
                    if (ct.Length == 0) errors.Add(at + ": the condition has no type");
                    else if (!KnownCondition(ct, server)) errors.Add(at + ": the condition type \"" + ct + "\" is not one the " + (server ? "server reads (its data ingest fails)" : "client reads (it drops the achievement and the rest of the file)"));
                    else if (!server)
                    {
                        string bad = ValidateCondition(ct, (c.Attr("target") ?? "").Trim());
                        if (bad != null) errors.Add(at + ": " + bad + ": the client drops the achievement and the rest of the file");
                    }
                    long cv;
                    string cvs = (c.Attr("value") ?? "").Trim();
                    if (cvs.Length > 0 && !long.TryParse(cvs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out cv)) errors.Add(at + ": the condition value \"" + cvs + "\" is not a number");
                }
                XmlElem r = Child(g, "reward");
                string rt = r == null ? "" : (r.Attr("type") ?? "").Trim();
                if (rt.Length > 0 && !KnownReward(rt))
                    (server ? errors : warnings).Add(at + ": the reward type \"" + rt + "\" is not one the " + (server ? "server reads (its data ingest fails)" : "client reads"));
                if (rt.Length > 0)
                    foreach (string f in new[] { "code", "value", "rank" })
                    {
                        string s = (r.Attr(f) ?? "").Trim();
                        long n;
                        if (s.Length > 0 && !long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) errors.Add(at + ": the reward " + f + " \"" + s + "\" is not a number");
                    }
                if ((string.Equals(rt, "item", StringComparison.OrdinalIgnoreCase) || string.Equals(rt, "title", StringComparison.OrdinalIgnoreCase)) && Neutral("reward@code", r.Attr("code")).Length == 0)
                    warnings.Add(at + ": the " + rt + " reward has no code");
            }
            // the client checks the lowest grade (m_nFirstGrade, achievedata.cpp Load), not the first in the file
            if (first != long.MaxValue && first != 1 && type != 9) warnings.Add("the lowest grade is " + first + ", not 1: the client logs it");
            if (!rising) warnings.Add("the grade values do not rise in the file's order");
        }

        /// <summary>The categoryTag values of a side's table/achievecategory.xml (the categories and their children); null when it has none.</summary>
        public static HashSet<string> CategoryTags(XmlDoc doc)
        {
            if (doc == null || doc.Root == null) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElem c in doc.Root.Children)
            {
                if (c.Name != "category") continue;
                string t = (c.Attr("categoryTag") ?? "").Trim();
                if (t.Length > 0) set.Add(t);
                foreach (XmlElem k in c.Children)
                {
                    string kt = k.Name == "child" ? (k.Attr("categoryTag") ?? "").Trim() : "";
                    if (kt.Length > 0) set.Add(kt);
                }
            }
            return set;
        }
    }

    /// <summary>One archive achievements are written to; Kms: the client's short notation.</summary>
    public sealed class AchieveSide
    {
        public string Archive;
        public bool Kms;
        public string NewLine { get { return Kms ? "\n" : "\r\n"; } }
    }

    /// <summary>One achievement: its id and the sides that ship it.</summary>
    public sealed class Achievement
    {
        public long Id;
        public string Entry;
        internal bool[] Ships;
        public bool IsAdded { get { foreach (bool b in Ships) if (b) return false; return true; } }
    }

    /// <summary>Edits achievements of a mod: each side's achieve/&lt;id&gt;.xml as a whole file of the mod, in that side's notation.</summary>
    public sealed class AchievementEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public readonly List<AchieveSide> Sides = new List<AchieveSide>();
        public readonly List<Achievement> All = new List<Achievement>();
        /// <summary>The other enabled mods (for the ids they take); null for none.</summary>
        public Func<IEnumerable<Mod>> Others;
        readonly TriggerEditor store;

        public AchievementEditor(DataSet data, Mod mod)
        {
            Data = data;
            Mod = mod;
            store = new TriggerEditor(data, mod);
            foreach (KeyValuePair<string, string> a in SideArchives(data, mod))
                Sides.Add(new AchieveSide { Archive = a.Key, Kms = Profile.OfArchive(a.Key) == Profile.KmsClient });
            if (Sides.Count == 0) throw new ModKitException("no target of the data set has an Xml.m2d with achievements");
            Load();
        }

        /// <summary>The kms client's and the server's Xml.m2d, and the global client's when the data set has no server
        /// (else the server's files are planned for the global client too) or when the mod has files of its own there.</summary>
        public static List<KeyValuePair<string, string>> SideArchives(DataSet data, Mod mod)
        {
            var l = new List<KeyValuePair<string, string>>();
            foreach (Profile p in new[] { Profile.KmsClient, Profile.Server, Profile.GmsClient })
            {
                if (!data.Has(p)) continue;
                string a = p.ArchiveIn("x:Xml.m2d");
                if (!data.Exists(a)) continue;
                if (p == Profile.GmsClient && data.Has(Profile.Server) && !HasFiles(mod, a)) continue;
                l.Add(new KeyValuePair<string, string>(a, AchieveRules.Folder));
            }
            return l;
        }

        static bool HasFiles(Mod mod, string archive)
        {
            if (mod == null) return false;
            foreach (ModOp op in mod.Ops)
            {
                var e = op as EntryOp;
                if (e != null && string.Equals(e.Archive, archive, StringComparison.OrdinalIgnoreCase) && AchieveRules.IsEntry(e.Name)) return true;
            }
            return false;
        }

        void Load()
        {
            All.Clear();
            var byId = new Dictionary<long, Achievement>();
            Func<long, string, Achievement> get = (id, entry) =>
            {
                Achievement a;
                if (!byId.TryGetValue(id, out a))
                {
                    byId[id] = a = new Achievement { Id = id, Entry = entry, Ships = new bool[Sides.Count] };
                    All.Add(a);
                }
                return a;
            };
            for (int i = 0; i < Sides.Count; i++)
                foreach (M2dEntry e in Data.Open(Sides[i].Archive).Entries)
                {
                    string n = e.Name.Replace('\\', '/');
                    long id = AchieveRules.IdOf(n);
                    if (id > 0) get(id, AchieveRules.EntryOf(id)).Ships[i] = true;
                }
            foreach (ModOp op in Mod.Ops)
            {
                var e = op as EntryOp;
                if (e == null || e.Delete || SideOf(e.Archive) < 0) continue;
                long id = AchieveRules.IdOf(e.Name);
                if (id > 0) get(id, AchieveRules.EntryOf(id));
            }
            All.Sort((x, y) => x.Id.CompareTo(y.Id));
        }

        int SideOf(string archive)
        {
            for (int i = 0; i < Sides.Count; i++) if (string.Equals(Sides[i].Archive, archive, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public Achievement Find(long id) { foreach (Achievement a in All) if (a.Id == id) return a; return null; }

        /// <summary>Achievements whose id or name holds the text (all for an empty text).</summary>
        public List<Achievement> Find(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return new List<Achievement>(All);
            var l = new List<Achievement>();
            foreach (Achievement a in All)
                if (a.Id.ToString(CultureInfo.InvariantCulture).Contains(text) || Text(a, "name").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) l.Add(a);
            return l;
        }

        // ---- reading ----

        /// <summary>The side's bytes as the mod leaves them; null when the side does not have it or the mod deletes it.</summary>
        public byte[] Bytes(Achievement a, int side)
        {
            EntryOp op = store.OpOf(Sides[side].Archive, a.Entry);
            if (op != null) return store.ModBytes(op);
            string n;
            return store.Shipped(Sides[side].Archive, a.Entry, out n);
        }

        public bool Has(Achievement a, int side) { return Bytes(a, side) != null; }

        /// <summary>The side's file, or null.</summary>
        public XmlDoc Doc(Achievement a, int side)
        {
            byte[] b = Bytes(a, side);
            if (b == null) return null;
            try { return XmlDoc.Parse(b, Sides[side].Archive + ":" + a.Entry); }
            catch (ModKitException) { return null; }
        }

        static XmlElem Top(XmlDoc d)
        {
            if (d == null || d.Root == null) return null;
            foreach (XmlElem e in d.Root.Children) if (e.Name == AchieveRules.Element) return e;
            return null;
        }

        /// <summary>The first side that has the achievement as a readable file, or -1.</summary>
        public int Shown(Achievement a)
        {
            for (int i = 0; i < Sides.Count; i++) if (Top(Doc(a, i)) != null) return i;
            return -1;
        }

        public bool IsDeleted(Achievement a)
        {
            for (int i = 0; i < Sides.Count; i++) if (Has(a, i)) return false;
            return true;
        }

        public bool IsChanged(Achievement a)
        {
            for (int i = 0; i < Sides.Count; i++) if (store.OpOf(Sides[i].Archive, a.Entry) != null) return true;
            return false;
        }

        /// <summary>A field in the editor's notation on one side ("" for none or the default).</summary>
        public string Get(Achievement a, int side, string field)
        {
            XmlElem e = Top(Doc(a, side));
            return e == null ? "" : AchieveRules.Neutral(field, e.Attr(field));
        }

        /// <summary>A field of the first side that has the achievement.</summary>
        public string Get(Achievement a, string field)
        {
            int s = Shown(a);
            return s < 0 ? "" : Get(a, s, field);
        }

        /// <summary>The grades of a side, one cell per AchieveRules.GradeFields column.</summary>
        public List<string[]> Grades(Achievement a, int side)
        {
            var l = new List<string[]>();
            XmlElem e = Top(Doc(a, side));
            if (e != null) foreach (XmlElem g in AchieveRules.Grades(e)) l.Add(AchieveRules.GradeCells(g));
            return l;
        }

        public List<string[]> Grades(Achievement a)
        {
            int s = Shown(a);
            return s < 0 ? new List<string[]>() : Grades(a, s);
        }

        /// <summary>The sides that have no file of the achievement (by archive).</summary>
        public List<string> MissingOn(Achievement a)
        {
            var l = new List<string>();
            if (IsDeleted(a)) return l;
            for (int i = 0; i < Sides.Count; i++) if (!Has(a, i)) l.Add(Sides[i].Archive);
            return l;
        }

        /// <summary>The fields and grade cells whose sides do not agree.</summary>
        public List<string> Differs(Achievement a)
        {
            var l = new List<string>();
            int first = -1;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (Top(Doc(a, i)) == null) continue;
                if (first < 0) { first = i; continue; }
                foreach (string f in AchieveRules.Fields)
                    if (Get(a, first, f) != Get(a, i, f) && !l.Contains(f)) l.Add(f);
                List<string[]> g0 = Grades(a, first), g1 = Grades(a, i);
                if (g0.Count != g1.Count) { if (!l.Contains("grades")) l.Add("grades"); continue; }
                for (int k = 0; k < g0.Count; k++)
                    for (int c = 0; c < g0[k].Length; c++)
                        if (g0[k][c] != g1[k][c])
                        {
                            string what = "grade " + (k + 1) + " " + AchieveRules.GradeFields[c];
                            if (!l.Contains(what)) l.Add(what);
                        }
            }
            return l;
        }

        // ---- writing ----

        void NotDeleted(Achievement a)
        {
            if (IsDeleted(a)) throw new ModKitException("achievement " + a.Id + " is removed; restore it first");
        }

        /// <summary>Applies an edit to the file of every side that has the achievement and stores them in the mod: all
        /// sides or none (an edit that throws on one side, a grade only one side has, say, leaves every side as it was).</summary>
        void EditAll(Achievement a, Action<int, XmlDoc, XmlElem, XmlEditor> edit)
        {
            var made = new List<KeyValuePair<int, byte[]>>();
            for (int i = 0; i < Sides.Count; i++)
            {
                if (!Has(a, i)) continue;
                XmlDoc d = Doc(a, i);
                XmlElem top = Top(d);
                if (top == null) throw new ModKitException(Sides[i].Archive + ": achievement " + a.Id + " has no readable <" + AchieveRules.Element + ">; it is not edited here");
                var ed = new XmlEditor(d);
                edit(i, d, top, ed);
                made.Add(new KeyValuePair<int, byte[]>(i, ed.ApplyBytes()));
            }
            foreach (KeyValuePair<int, byte[]> m in made) store.Put(Sides[m.Key].Archive, a.Entry, m.Value);
        }

        static void Write(XmlEditor ed, XmlElem e, string attr, string field, string value, bool kms)
        {
            value = (value ?? "").Trim();
            if (kms)
            {
                if (value.Length == 0 || value == AchieveRules.DefaultOf(field)) ed.RemoveAttr(e, attr);
                else ed.SetAttr(e, attr, value);
            }
            else
            {
                string v = value.Length > 0 ? value : AchieveRules.DefaultOf(field);
                if (v.Length == 0 && e.Attr(attr) == null) return;
                ed.SetAttr(e, attr, v);
            }
        }

        /// <summary>Sets a field (AchieveRules.Fields), in the editor's notation, on every side that has the achievement.</summary>
        public void Set(Achievement a, string field, string value)
        {
            if (Array.IndexOf(AchieveRules.Fields, field) < 0)
                throw new ModKitException("\"" + field + "\" is not a field of an achievement here" + (field == "id" ? "; clone it for another id" : ""));
            NotDeleted(a);
            EditAll(a, (i, d, top, ed) => Write(ed, top, field, field, value, Sides[i].Kms));
        }

        /// <summary>Sets a grade cell (AchieveRules.GradeFields) of the index-th grade on every side that has it. An
        /// empty reward type takes the reward away (the short notation drops it, the full one writes the empty reward).</summary>
        public void SetGrade(Achievement a, int index, string field, string value)
        {
            int col = Array.IndexOf(AchieveRules.GradeFields, field);
            if (col < 0) throw new ModKitException("\"" + field + "\" is not a grade field");
            NotDeleted(a);
            value = (value ?? "").Trim();
            if (col == 0 && value.Length == 0) throw new ModKitException("a grade needs a value");
            EditAll(a, (i, d, top, ed) =>
                {
                    AchieveSide side = Sides[i];
                    List<XmlElem> grades = AchieveRules.Grades(top);
                    if (index < 0 || index >= grades.Count) throw new ModKitException(side.Archive + ": achievement " + a.Id + " has no grade " + (index + 1));
                    XmlElem g = grades[index];
                    if (col == 0) { ed.SetAttr(g, "value", value); return; }
                    string attr = field.Substring(field.IndexOf('@') + 1), el = field.Substring(0, field.IndexOf('@'));
                    XmlElem e = AchieveRules.Child(g, el);
                    if (el == "condition")
                    {
                        if (e == null) throw new ModKitException(side.Archive + ": grade " + (index + 1) + " of achievement " + a.Id + " has no condition");
                        Write(ed, e, attr, field, value, side.Kms);
                        return;
                    }
                    // the reward
                    string[] cells = AchieveRules.GradeCells(g);
                    cells[col] = value;
                    if (cells[5].Length == 0)
                    {
                        if (col != 5 && value.Length > 0) throw new ModKitException("set the reward type first");
                        for (int c = 6; c <= 8; c++) cells[c] = "";
                    }
                    if (e == null)
                    {
                        if (cells[5].Length == 0) return;
                        string[] parts = AchieveRules.GradeText(cells, !side.Kms, "\n").Split('\n');
                        foreach (string p in parts) if (p.TrimStart().StartsWith("<reward")) ed.Append(g, p.Trim(), "");
                        return;
                    }
                    if (side.Kms && cells[5].Length == 0) { ed.Delete(e); return; }
                    for (int c = 5; c <= 8; c++)
                    {
                        string at = AchieveRules.GradeFields[c].Substring(7);
                        Write(ed, e, at, AchieveRules.GradeFields[c], cells[c], side.Kms);
                    }
                });
        }

        /// <summary>Adds a grade after the last one: a copy of it with the next value and no reward.</summary>
        public void AddGrade(Achievement a)
        {
            NotDeleted(a);
            EditAll(a, (i, d, top, ed) =>
                {
                    bool full = !Sides[i].Kms;
                    List<XmlElem> grades = AchieveRules.Grades(top);
                    if (grades.Count == 0) throw new ModKitException("achievement " + a.Id + " has no grade to copy");
                    long max = 0;
                    foreach (XmlElem g in grades) { long v; if (long.TryParse(g.Attr("value") ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)) max = Math.Max(max, v); }
                    string[] cells = AchieveRules.GradeCells(grades[grades.Count - 1]);
                    cells[0] = (max + 1).ToString(CultureInfo.InvariantCulture);
                    for (int c = 5; c <= 8; c++) cells[c] = "";
                    ed.InsertAfter(grades[grades.Count - 1], AchieveRules.GradeText(cells, full, "\n"), "");
                });
        }

        /// <summary>Removes the index-th grade on every side; the last grade stays.</summary>
        public void RemoveGrade(Achievement a, int index)
        {
            NotDeleted(a);
            EditAll(a, (i, d, top, ed) =>
                {
                    List<XmlElem> grades = AchieveRules.Grades(top);
                    if (grades.Count <= 1) throw new ModKitException("achievement " + a.Id + " keeps at least one grade");
                    if (index < 0 || index >= grades.Count) throw new ModKitException(Sides[i].Archive + ": achievement " + a.Id + " has no grade " + (index + 1));
                    ed.Delete(grades[index]);
                });
        }

        // ---- new ids ----

        IEnumerable<Mod> OtherMods()
        {
            var l = new List<Mod>();
            if (Others != null) foreach (Mod m in Others()) if (m != Mod && m.Id != Mod.Id) l.Add(m);
            return l;
        }

        /// <summary>Throws when a new achievement cannot take the id.</summary>
        public void CheckNewId(long id)
        {
            if (!AchieveRules.InNewRange(id))
                throw new ModKitException("new achievement ids are " + AchieveRules.NewFrom(1) + "-" + AchieveRules.NewTo(1) + ", " + AchieveRules.NewFrom(2) + "-" + AchieveRules.NewTo(2) + " and "
                    + AchieveRules.NewFrom(3) + "-" + AchieveRules.NewTo(3) + " (combat, adventure, living); " + id + " is outside");
            if (Find(id) != null) throw new ModKitException("achievement " + id + " is there already");
            foreach (Mod m in OtherMods())
            {
                if (AddedIds(m).Contains(id)) throw new ModKitException("the enabled mod " + m.Id + " adds an achievement " + id + " already");
                foreach (IdRange r in m.Ids)
                    if (r.Space == AchieveRules.Space && id >= r.From && id <= r.To)
                        throw new ModKitException("the achievement id " + id + " is reserved by the enabled mod " + m.Id);
            }
        }

        /// <summary>The achievement ids a mod adds: its achieve/ files that no archive ships.</summary>
        public static List<long> AddedIds(Mod mod)
        {
            var l = new List<long>();
            foreach (ModOp op in mod.Ops)
            {
                var e = op as EntryOp;
                if (e == null || e.Delete) continue;
                long id = AchieveRules.IdOf(e.Name);
                if (id > 0 && AchieveRules.InNewRange(id) && !l.Contains(id)) l.Add(id);
            }
            return l;
        }

        /// <summary>The lowest free id of a category's range (1 combat, 2 adventure, 3 living).</summary>
        public long NextId(int category)
        {
            if (category < 1 || category > 3) throw new ModKitException("the category is 1 (combat), 2 (adventure) or 3 (living)");
            var taken = new HashSet<long>();
            foreach (Achievement a in All) taken.Add(a.Id);
            foreach (Mod m in OtherMods())
            {
                foreach (long id in AddedIds(m)) taken.Add(id);
                foreach (IdRange r in m.Ids)
                    if (r.Space == AchieveRules.Space)
                        for (long id = Math.Max(r.From, AchieveRules.NewFrom(category)); id <= Math.Min(r.To, AchieveRules.NewTo(category)); id++) taken.Add(id);
            }
            for (long id = AchieveRules.NewFrom(category); id <= AchieveRules.NewTo(category); id++) if (!taken.Contains(id)) return id;
            throw new ModKitException("the id range of new " + AchieveRules.Categories[category] + " achievements is full");
        }

        // ---- clone, remove ----

        static byte[] Encode(string text) { return new UTF8Encoding(false).GetBytes(text); }

        /// <summary>A new achievement that copies source as shown (each side its own file with the new id; a side
        /// without the source gets the shown side's file in its own notation; name and texts in each language).</summary>
        public Achievement Clone(Achievement source, long newId)
        {
            NotDeleted(source);
            CheckNewId(newId);
            int shown = Shown(source);
            if (shown < 0) throw new ModKitException("achievement " + source.Id + " has no readable file to copy");
            XmlElem from = Top(Doc(source, shown));
            var made = new Achievement { Id = newId, Entry = AchieveRules.EntryOf(newId), Ships = new bool[Sides.Count] };
            string id = newId.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < Sides.Count; i++)
            {
                XmlDoc d = Doc(source, i);
                XmlElem top = Top(d);
                byte[] bytes;
                if (top != null)
                {
                    var ed = new XmlEditor(d);
                    ed.SetAttr(top, "id", id);
                    bytes = ed.ApplyBytes();
                }
                else bytes = Encode(AchieveRules.FileText(from, newId, !Sides[i].Kms, Sides[i].NewLine));
                store.Put(Sides[i].Archive, made.Entry, bytes);
            }
            IdSpaces.Reserve(Mod, AchieveRules.Space, newId);
            All.Add(made);
            All.Sort((x, y) => x.Id.CompareTo(y.Id));
            foreach (string f in TextFields)
            {
                string shownText = Text(source, f);
                for (int i = 0; i < Sides.Count; i++)
                    foreach (string lang in Languages(Sides[i].Archive))
                    {
                        string t = Has(source, i) ? TextAt(i, lang, source.Id, f) : "";
                        if (t.Length == 0) t = shownText;
                        if (t.Length > 0) SetTextAt(i, lang, newId, f, t);
                    }
            }
            return made;
        }

        /// <summary>Removes an achievement: a new one goes away with its texts and its id is free again; a shipped one
        /// is deleted on every side (an entry delete) and can be restored.</summary>
        public void Remove(Achievement a)
        {
            if (a.IsAdded)
            {
                foreach (string f in TextFields) SetText(a, f, "");
                for (int i = 0; i < Sides.Count; i++) store.Revert(Sides[i].Archive, a.Entry);
                All.Remove(a);
                IdSpaces.Unreserve(Mod, AchieveRules.Space, a.Id);
                return;
            }
            for (int i = 0; i < Sides.Count; i++)
            {
                if (a.Ships[i])
                {
                    EntryOp op = store.OpOf(Sides[i].Archive, a.Entry);
                    if (op == null)
                    {
                        string n;
                        store.Shipped(Sides[i].Archive, a.Entry, out n);
                        op = new EntryOp { Archive = Sides[i].Archive, Name = n };
                        Mod.Ops.Add(op);
                    }
                    else if (op.File != null) Mod.Files.Remove(op.File.Replace('\\', '/'));
                    op.File = null; op.Copy = null; op.Delete = true;
                }
                else store.Revert(Sides[i].Archive, a.Entry);
            }
        }

        /// <summary>Takes back the removal of a shipped achievement (its files as shipped).</summary>
        public void Restore(Achievement a)
        {
            for (int i = 0; i < Sides.Count; i++)
            {
                EntryOp op = store.OpOf(Sides[i].Archive, a.Entry);
                if (op != null && op.Delete) store.Revert(Sides[i].Archive, a.Entry);
            }
        }

        /// <summary>Drops every change the mod makes to a shipped achievement: its files and its texts.</summary>
        public void Revert(Achievement a)
        {
            if (a.IsAdded) throw new ModKitException("achievement " + a.Id + " is new; remove it instead");
            for (int i = 0; i < Sides.Count; i++) store.Revert(Sides[i].Archive, a.Entry);
            string id = a.Id.ToString(CultureInfo.InvariantCulture);
            // every text table of every side and language, not only the ones read so far: after a reload Text() has
            // read only the first side that has the text, and the other sides' edits must go too
            for (int i = 0; i < Sides.Count; i++)
                foreach (string lang in Languages(Sides[i].Archive))
                {
                    Names(Sides[i].Archive, lang, AchieveRules.NameFile);
                    Names(Sides[i].Archive, lang, AchieveRules.DescFile);
                }
            foreach (NameTable n in names.Values)
            {
                List<TableRow> rows;
                if (n == null || !n.ById.TryGetValue(id, out rows)) continue;
                foreach (TableRow r in rows.ToArray())
                {
                    if (r.IsAdded) { n.Draft.Delete(r); rows.Remove(r); }
                    else n.Draft.RevertCells(r);
                }
                if (rows.Count == 0) n.ById.Remove(id);
            }
        }

        // ---- name and descriptions ----

        /// <summary>The texts: name (achievename.xml) and the four of achievedescription.xml.</summary>
        public static readonly string[] TextFields = { "name", "desc", "complete", "manualDesc", "manualComplete" };

        static string FileOfText(string field) { return field == "name" ? AchieveRules.NameFile : AchieveRules.DescFile; }
        static string RowOfText(string field) { return field == "name" ? "key" : "achieve"; }

        sealed class NameTable
        {
            internal TableDraft Draft;
            internal readonly Dictionary<string, List<TableRow>> ById = new Dictionary<string, List<TableRow>>();
        }

        readonly Dictionary<string, NameTable> names = new Dictionary<string, NameTable>();
        readonly Dictionary<string, List<string>> langs = new Dictionary<string, List<string>>();
        /// <summary>The language files that could not be read so far, one line each (Check lists them too).</summary>
        public readonly List<string> TextProblems = new List<string>();

        /// <summary>The language folders of an archive that hold string/{lang}/achievename.xml.</summary>
        public List<string> Languages(string archive)
        {
            List<string> list;
            if (langs.TryGetValue(archive, out list)) return list;
            list = new List<string>();
            var rx = new Regex("^string/([^/]+)/" + Regex.Escape(AchieveRules.NameFile) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (M2dEntry e in Data.Open(archive).Entries)
            {
                Match m = rx.Match(e.Name.Replace('\\', '/'));
                if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            langs[archive] = list;
            return list;
        }

        NameTable Names(string archive, string lang, string file)
        {
            string key = archive + "\n" + lang + "\n" + file;
            NameTable n;
            if (names.TryGetValue(key, out n)) return n;
            string entry = "string/" + lang + "/" + file;
            string row = file == AchieveRules.NameFile ? "key" : "achieve";
            if (Data.Open(archive).Find(entry) == null) { names[key] = null; return null; }
            try
            {
                n = new NameTable { Draft = new TableDraft(Data, Mod, archive, entry, null) };
                foreach (TableRow r in n.Draft.Rows)
                {
                    if (r.Base.Name != row || r.IsDeleted) continue;
                    string id = n.Draft.Get(r, "id") ?? "";
                    List<TableRow> rows;
                    if (!n.ById.TryGetValue(id, out rows)) n.ById[id] = rows = new List<TableRow>();
                    rows.Add(r);
                }
            }
            catch (ModKitException ex)
            {
                n = null;
                TextProblems.Add(archive + ": " + entry + " could not be read (" + ex.Message + "): texts are not written there");
            }
            names[key] = n;
            return n;
        }

        static void CheckText(string field)
        {
            if (Array.IndexOf(TextFields, field) < 0) throw new ModKitException("an achievement has the texts " + string.Join(", ", TextFields) + "; not \"" + field + "\"");
        }

        /// <summary>A text: the English one of the first side that has it (else its first language); "" for none.</summary>
        public string Text(Achievement a, string field)
        {
            CheckText(field);
            for (int i = 0; i < Sides.Count; i++)
            {
                List<string> l = Languages(Sides[i].Archive);
                if (l.Count == 0) continue;
                string t = TextAt(i, l.Contains("en") ? "en" : l[0], a.Id, field);
                if (t.Length > 0) return t;
            }
            return "";
        }

        /// <summary>Sets a text in every language of every side that has the achievement.</summary>
        public void SetText(Achievement a, string field, string value)
        {
            CheckText(field);
            value = value ?? "";
            for (int i = 0; i < Sides.Count; i++)
            {
                if (!Has(a, i) && value.Length > 0) continue;
                foreach (string lang in Languages(Sides[i].Archive)) SetTextAt(i, lang, a.Id, field, value);
            }
        }

        string TextAt(int side, string lang, long id, string field)
        {
            NameTable n = Names(Sides[side].Archive, lang, FileOfText(field));
            List<TableRow> rows;
            if (n == null || !n.ById.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out rows)) return "";
            foreach (TableRow r in rows)
            {
                string v = n.Draft.Get(r, field);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        void SetTextAt(int side, string lang, long nid, string field, string value)
        {
            NameTable n = Names(Sides[side].Archive, lang, FileOfText(field));
            if (n == null) return;
            string id = nid.ToString(CultureInfo.InvariantCulture);
            List<TableRow> rows;
            if (n.ById.TryGetValue(id, out rows))
            {
                foreach (TableRow r in rows.ToArray())
                {
                    n.Draft.Set(r, field, value.Length == 0 ? null : value);
                    bool empty = true;
                    foreach (string f in TextFields) if (FileOfText(f) == FileOfText(field) && !string.IsNullOrEmpty(n.Draft.Get(r, f))) empty = false;
                    if (r.IsAdded && empty) { n.Draft.Delete(r); rows.Remove(r); }
                }
                if (rows.Count == 0) n.ById.Remove(id);
            }
            else if (value.Length > 0)
                n.ById[id] = new List<TableRow> { n.Draft.AddRow("<" + RowOfText(field) + " id=\"" + XmlDoc.EscapeAttr(id, '"') + "\" " + field + "=\"" + XmlDoc.EscapeAttr(value, '"') + "\" />") };
        }

        // ---- checks ----

        readonly Dictionary<int, HashSet<string>> tags = new Dictionary<int, HashSet<string>>();

        HashSet<string> Tags(int side)
        {
            HashSet<string> t;
            if (tags.TryGetValue(side, out t)) return t;
            string n;
            byte[] b = store.Shipped(Sides[side].Archive, AchieveRules.CategoryEntry, out n);
            EntryOp op = store.OpOf(Sides[side].Archive, AchieveRules.CategoryEntry);
            if (op != null) b = store.ModBytes(op);
            try { t = b == null ? null : AchieveRules.CategoryTags(XmlDoc.Parse(b, AchieveRules.CategoryEntry)); }
            catch (ModKitException) { t = null; }
            tags[side] = t;
            return t;
        }

        /// <summary>The categoryTag values the lists show (of the first side that has table/achievecategory.xml).</summary>
        public List<string> CategoryTags()
        {
            var l = new List<string>();
            for (int i = 0; i < Sides.Count; i++)
            {
                HashSet<string> t = Tags(i);
                if (t == null) continue;
                l.AddRange(t);
                break;
            }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        /// <summary>What is wrong with the achievement as the mod leaves it, side by side.</summary>
        public List<string> Check(Achievement a)
        {
            var l = new List<string>();
            if (IsDeleted(a)) return l;
            for (int i = 0; i < Sides.Count; i++)
            {
                if (!Has(a, i)) continue;
                var errors = new List<string>();
                var warnings = new List<string>();
                XmlDoc d = Doc(a, i);
                if (d == null) errors.Add("the file is not readable XML");
                else AchieveRules.CheckFile(d, a.Id, !Sides[i].Kms, Tags(i), errors, warnings);
                foreach (string t in errors) { string line = Sides[i].Archive + ": " + t; if (!l.Contains(line)) l.Add(line); }
                foreach (string t in warnings) { string line = Sides[i].Archive + ": " + t; if (!l.Contains(line)) l.Add(line); }
            }
            foreach (string s in MissingOn(a))
                l.Add("not in " + s + (Profile.OfArchive(s) == Profile.KmsClient ? ": the server grants it but the client never shows it" : ": the client shows it but the server never grants it"));
            foreach (string f in Differs(a)) l.Add("the sides differ in " + f);
            foreach (string t in TextProblems) if (!l.Contains(t)) l.Add(t);
            return l;
        }
    }
}
