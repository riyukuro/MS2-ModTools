// The skill and effect editor behind the Skills view: reads shipped skills (PlannerSkillsRead.cs) and writes the
// mod's <skill>, <effect> and <jobskill> ops (Skills.cs). Nothing here touches an archive; Plan/Apply do that.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS2.ModKit
{
    public sealed class SkillEditor
    {
        public readonly DataSet Data;
        public readonly Mod Mod;
        public SkillMap Map = SkillMap.Default;
        /// <summary>The language of the names Find and Open give: null = each target's own (Korean on the kms-client, English on the others).</summary>
        public string NameLanguage;
        /// <summary>After Find: the language each target's names were read in ("kms-client kr, gms-client en, ...").</summary>
        public string NamesUsed;

        public SkillEditor(DataSet data, Mod mod)
        {
            if (mod == null) throw new ArgumentNullException("mod");
            Data = data; Mod = mod;
        }

        public List<KeyValuePair<long, string>> Find(SkillKind kind, string text, int max)
        {
            string used;
            List<KeyValuePair<long, string>> l = Planner.SkillFind(Data, kind, text, max, NameLanguage, out used);
            NamesUsed = used;
            return l;
        }

        /// <summary>The languages the targets have names in.</summary>
        public List<string> NameLanguages(SkillKind kind) { return Planner.SkillNameLanguages(Data, kind); }

        /// <summary>The shipped skill or effect, null when no target ships the id (a clone of the mod: open its source).</summary>
        public SkillInfo Open(SkillKind kind, long id) { return Planner.SkillOpen(Data, kind, id, NameLanguage); }

        /// <summary>The field names of a scope, in map order.</summary>
        public string[] Fields(SkillKind kind, SkillScope scope) { return Map.Entity(kind).FieldNames(scope); }

        public SkillField Field(SkillKind kind, string name) { return Map.Entity(kind).Field(name); }

        /// <summary>The mod's op of an id, or null; create = add one when there is none.</summary>
        public SkillOp Op(SkillKind kind, long id, bool create)
        {
            foreach (SkillOp s in Mod.Skills) if (s.Kind == kind && s.Id == id) return s;
            if (!create) return null;
            var op = new SkillOp { Kind = kind, Id = id };
            Mod.Skills.Add(op);
            return op;
        }

        static string LevelText(int level) { return level.ToString(CultureInfo.InvariantCulture); }

        SkillSet FindSet(SkillOp op, SkillScope scope, int level, int motion, int attack, bool create)
        {
            string lv = LevelText(level);
            foreach (SkillSet s in op.Sets)
                if (s.Scope == scope && s.Levels == lv && (scope == SkillScope.Level || s.Motion == motion) && (scope != SkillScope.Attack || s.Attack == attack))
                    return s;
            if (!create) return null;
            var n = new SkillSet { Scope = scope, Levels = lv, Motion = scope == SkillScope.Level ? 0 : motion, Attack = scope == SkillScope.Attack ? attack : 0 };
            op.Sets.Add(n);
            return n;
        }

        /// <summary>The mod's value of one field at one level (motion, attack), or null when the mod leaves it.</summary>
        public string Get(SkillKind kind, long id, int level, int motion, int attack, string field)
        {
            SkillOp op = Op(kind, id, false);
            if (op == null) return null;
            SkillField f = Field(kind, field);
            if (f == null) return null;
            string v = null;
            // the last set that names this level wins, as in the planner
            foreach (SkillSet s in op.Sets)
            {
                if (s.Scope != f.Scope || !SkillLevels.Has(s.Levels, level)) continue;
                if (f.Scope != SkillScope.Level && s.Motion != motion) continue;
                if (f.Scope == SkillScope.Attack && s.Attack != attack) continue;
                foreach (KeyValuePair<string, string> kv in s.Fields) if (kv.Key == field) v = kv.Value;
            }
            return v;
        }

        /// <summary>
        /// Sets one field at one level (motion, attack: 1-based, used by their scopes) in the mod: a number in
        /// neutral units or "*k". An empty value removes the mod's value of that exact level. Throws on a bad value.
        /// </summary>
        public void Set(SkillKind kind, long id, int level, int motion, int attack, string field, string value)
        {
            SkillField f = Field(kind, field);
            if (f == null) throw new ModKitException("no " + (kind == SkillKind.Skill ? "skill" : "effect") + " field \"" + field + "\"");
            value = (value ?? "").Trim();
            bool clear = value.Length == 0;
            if (!clear && !SkillValue.Valid(value)) throw new ModKitException(field + "=\"" + value + "\": not a number or *factor");
            SkillOp op = Op(kind, id, !clear);
            if (op == null) return;
            SkillSet s = FindSet(op, f.Scope, level, motion, attack, !clear);
            if (s == null) return;
            s.Fields.RemoveAll(kv => kv.Key == field);
            if (!clear) s.Fields.Add(new KeyValuePair<string, string>(field, value));
            if (s.Fields.Count == 0) op.Sets.Remove(s);
            Prune(op);
        }

        /// <summary>Sets the tooltip of one level: the kms-client stringParam and the gms/server text (null: leave).</summary>
        public void SetTooltip(SkillKind kind, long id, int level, string kmsParam, string gmsText) { SetTooltip(kind, id, level, kmsParam, gmsText, null); }

        /// <summary>The same; lang = the language gmsText goes to (null: en).</summary>
        public void SetTooltip(SkillKind kind, long id, int level, string kmsParam, string gmsText, string lang)
        {
            if (!string.IsNullOrEmpty(lang) && !System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z]+$")) throw new ModKitException("lang=\"" + lang + "\": a language code such as en");
            SkillOp op = Op(kind, id, true);
            string lv = LevelText(level);
            op.Texts.RemoveAll(t => t.Levels == lv);
            if (!string.IsNullOrEmpty(kmsParam) || !string.IsNullOrEmpty(gmsText))
                op.Texts.Add(new SkillText { Levels = lv, KmsParam = string.IsNullOrEmpty(kmsParam) ? null : kmsParam, GmsText = string.IsNullOrEmpty(gmsText) ? null : gmsText, Lang = string.IsNullOrEmpty(lang) ? "en" : lang });
            Prune(op);
        }

        void Prune(SkillOp op)
        {
            if (!op.IsClone && op.Sets.Count == 0 && op.Raws.Count == 0 && op.Texts.Count == 0 && op.Swaps.Count == 0) Mod.Skills.Remove(op);
        }

        /// <summary>
        /// A new skill or effect copied from a shipped one on each target that has it. levels null: all; name null:
        /// the source's; own: source effect id -> new effect id, effects cloned with it and re-pointed.
        /// </summary>
        public SkillOp Clone(SkillKind kind, long source, long newId, string levels, string name, IEnumerable<KeyValuePair<long, long>> own)
        { return Clone(kind, source, newId, levels, name, own, null); }

        /// <summary>The same; cooldownGroup = "own" (the new id), a group id, or null (keep the source's; Plan warns).</summary>
        public SkillOp Clone(SkillKind kind, long source, long newId, string levels, string name, IEnumerable<KeyValuePair<long, long>> own, string cooldownGroup)
        {
            if (string.IsNullOrEmpty(cooldownGroup)) cooldownGroup = null;
            if (cooldownGroup != null && kind != SkillKind.Skill) throw new ModKitException("cooldown-group belongs to a skill");
            if (cooldownGroup != null && cooldownGroup != "own" && !System.Text.RegularExpressions.Regex.IsMatch(cooldownGroup, "^[0-9]{1,10}$")) throw new ModKitException("cooldown-group \"" + cooldownGroup + "\": own or a group id");
            if (Op(kind, newId, false) != null) throw new ModKitException("the mod already has " + (kind == SkillKind.Skill ? "skill " : "effect ") + newId);
            if (levels != null) SkillLevels.Parse(levels);
            var op = new SkillOp { Kind = kind, Id = newId, Clone = source, Levels = string.IsNullOrEmpty(levels) ? null : levels, Name = string.IsNullOrEmpty(name) ? null : name, CooldownGroup = cooldownGroup };
            if (own != null) foreach (KeyValuePair<long, long> kv in own) op.Own.Add(kv);
            Mod.Skills.Add(op);
            return op;
        }

        /// <summary>A job skill tree row: replace = the shipped skill whose slot it takes (-1: a new row at position "x,y").</summary>
        public JobSkillOp JobRow(int job, long skill, long replace, string position, int tier)
        {
            if (replace < 0 && string.IsNullOrEmpty(position)) throw new ModKitException("a new job skill row needs a position \"x,y\"");
            var j = new JobSkillOp { Job = job, Skill = skill, Replace = replace, Position = replace < 0 ? position : null, Tier = tier < 1 ? 1 : tier };
            Mod.JobSkills.RemoveAll(o => o.Job == job && o.Skill == skill);
            Mod.JobSkills.Add(j);
            return j;
        }
    }
}
