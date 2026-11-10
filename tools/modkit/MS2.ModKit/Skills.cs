// Player skills and their additional effects (buffs): "one id, levels inside, nested rows by position".
//
//   kms-client   skilldata/*.xml bundles (rows <skill id>) and additional/*.xml bundles (rows <additional id>).
//                Every .xml of those folders is read, so the file name does not matter. A level is
//                <level value> (skill) or <level level> (effect).
//   gms-client,  one file per id: skill/<id/1000000>/<id>.xml and additionaleffect/<id>.xml. A level is
//   server       <level> with @value (skill) or BasicProperty@level (effect); one level may be written several
//                times for features and locales, and the game reads the variant FeatureGate.Pick chooses.
//
// Motions are the <motion> elements of a level, attacks the <attack> elements of a motion; both are told apart by
// position only (1 = the first). Where each field sits on each side is in Maps/skill.skillmap.xml (SkillMap).
//
// A skill mod (mod.xml):
//   <skill id="..."> or <skill id="new id" clone="shipped id" [levels="1-3"] [name="..."] [cooldown-group="own|id"]>
//                                       cooldown-group: the clone's cooldownGroupID ("own" = the new id, 0 = none);
//                                       left out, the clone keeps its source's group (Plan warns when it is not 0)
//     <level value="1|1,2|1-4|*" cooldown="3" sp="10" ep="*0.5"/>        fields of the level (neutral units)
//     <motion level="1" index="1" speed="1.2"/>                             fields of a motion
//     <attack level="1" motion="1" index="1" damage="*2" distance="900"/>   fields of an attack
//     <tooltip level="1" kms-param="..." gms-text="..." [lang="en"]/>       tooltip text, set explicitly; gms-text
//                                                                           goes to the one language lang (default en)
//     <raw side="server" level="1" at="beginCondition"><set name="..." value="..."/></raw>
//     <swap level="1" motion="1" from-skill="..." from-level="2" from-motion="1"/>
//     <own effect="shipped effect id" as="new effect id"/>                  clone: effects that get their own copy
//   </skill>
//   <effect id="..."> ... the same with duration/interval/stacks on <level> ... </effect>
//   <jobskill job="10" skill="..." replace="..."/> or <jobskill job="10" skill="..." tier="1" position="5,6"/>
// A value "*k" multiplies each side's shipped value by k. Times are written in seconds and converted per side.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MS2.ModKit.M2d;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    public enum SkillKind { Skill, Effect }
    public enum SkillScope { Level, Motion, Attack }

    /// <summary>Fields of the levels, a motion or an attack.</summary>
    public sealed class SkillSet
    {
        public SkillScope Scope;
        /// <summary>The levels: null or "*" = all; "1", "1,3", "2-4".</summary>
        public string Levels;
        /// <summary>1-based; 0 when the scope does not use it.</summary>
        public int Motion, Attack;
        /// <summary>Neutral field name -> value ("3", "*2").</summary>
        public readonly List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        public int Line;
    }

    /// <summary>An attribute of one side only, by a selector under the level.</summary>
    public sealed class SkillRaw
    {
        public string Side;     // profile name: kms-client, gms-client, server
        public string Levels;
        public string At = "";
        public readonly List<AttrOp> Attrs = new List<AttrOp>();
        public int Line;
    }

    public sealed class SkillText
    {
        public string Levels;
        public string KmsParam;     // kms-client level@stringParam
        public string GmsText;      // gms-client and server description text of the level
        public string Lang = "en";  // the one language GmsText is written to (string/<lang>/...)
        public int Line;
    }

    /// <summary>A motion block of the level replaced by a shipped skill level's motion block (same side, by position).</summary>
    public sealed class SkillSwap
    {
        public string Levels;
        public int Motion;
        public long FromSkill;
        public int FromLevel, FromMotion;
        public int Line;
    }

    public sealed class SkillOp
    {
        public SkillKind Kind;
        public long Id;
        /// <summary>The shipped id this one is cloned from, or -1 for an edit of a shipped id.</summary>
        public long Clone = -1;
        /// <summary>Clone: the levels to keep (null = all).</summary>
        public string Levels;
        /// <summary>Clone: the name of the new id (null = the source's).</summary>
        public string Name;
        /// <summary>Clone: basic/stateAttr@cooldownGroupID of the new id: "own" (the new id), a number, or null (keep the source's).</summary>
        public string CooldownGroup;
        /// <summary>Clone: shipped effect id -> new effect id.</summary>
        public readonly List<KeyValuePair<long, long>> Own = new List<KeyValuePair<long, long>>();
        public readonly List<SkillSet> Sets = new List<SkillSet>();
        public readonly List<SkillRaw> Raws = new List<SkillRaw>();
        public readonly List<SkillText> Texts = new List<SkillText>();
        public readonly List<SkillSwap> Swaps = new List<SkillSwap>();
        public int Line;
        /// <summary>Set by the planner for an own effect of a clone: the skill op it comes from.</summary>
        internal SkillOp OwnedBy;

        public bool IsClone { get { return Clone >= 0; } }
        public string Tag { get { return Kind == SkillKind.Skill ? "skill" : "effect"; } }
    }

    /// <summary>A row of a job's skill tree: a shipped slot gets another skill, or a new row at a position.</summary>
    public sealed class JobSkillOp
    {
        /// <summary>The job group as kms-client numbers it (10); gms-client and server write it with three digits ("010").</summary>
        public int Job;
        public long Skill;
        /// <summary>The skill in the shipped slot to replace, or -1 to add a row.</summary>
        public long Replace = -1;
        /// <summary>Add: 1 = first job, 2 = second job (kms jobLevel; gms/server rows with subJobCode).</summary>
        public int Tier = 1;
        /// <summary>Add: "x,y".</summary>
        public string Position;
        public string MaxLevel, LearnLevel, RequireSkills, RequireLevels;
        public int Line;
    }

    public sealed partial class Mod
    {
        /// <summary>Skill and effect ops (Skills.cs).</summary>
        public readonly List<SkillOp> Skills = new List<SkillOp>();
        /// <summary>Job skill tree rows (Skills.cs).</summary>
        public readonly List<JobSkillOp> JobSkills = new List<JobSkillOp>();

        sealed partial class Reader
        {
            static int Index(XmlElem e, string name, Reader r, bool need)
            {
                string v = e.Attr(name);
                if (v == null) { if (need) throw r.Bad(e, "<" + e.Name + "> needs " + name + "=\"n\" (1 = the first)"); return 0; }
                int n;
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1) throw r.Bad(e, name + "=\"" + v + "\" is not a position from 1");
                return n;
            }

            string LevelsOf(XmlElem e, string name)
            {
                string v = e.Attr(name);
                if (v == null) return null;
                try { SkillLevels.Parse(v); } catch (ModKitException x) { throw Bad(e, x.Message); }
                return v;
            }

            long Id(XmlElem e, string name)
            {
                long v;
                string s = Need(e, name);
                if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v)) throw Bad(e, name + "=\"" + s + "\" is not an id");
                return v;
            }

            void Fields(XmlElem e, SkillSet s, SkillKind kind, string[] skip)
            {
                foreach (XmlAttr a in e.Attrs)
                {
                    if (Array.IndexOf(skip, a.Name) >= 0) continue;
                    SkillField f = SkillMap.Default.Entity(kind).Field(a.Name);
                    if (f == null || f.Scope != s.Scope)
                        throw Bad(e, "<" + e.Name + "> of a" + (kind == SkillKind.Skill ? " skill" : "n effect") + " has no field \"" + a.Name + "\" (known: "
                            + string.Join(", ", SkillMap.Default.Entity(kind).FieldNames(s.Scope)) + ")");
                    if (!SkillValue.Valid(a.Value)) throw Bad(e, a.Name + "=\"" + a.Value + "\" is neither a number nor *factor");
                    s.Fields.Add(new KeyValuePair<string, string>(a.Name, a.Value));
                }
                if (s.Fields.Count == 0) throw Bad(e, "<" + e.Name + "> sets no field");
            }

            internal SkillOp ReadSkill(XmlElem e, SkillKind kind)
            {
                Only(e, "id", "clone", "levels", "name", "cooldown-group");
                var op = new SkillOp { Kind = kind, Id = Id(e, "id"), Line = e.Line, Name = e.Attr("name"), Levels = LevelsOf(e, "levels"), CooldownGroup = e.Attr("cooldown-group") };
                if (e.Attr("clone") != null) op.Clone = Id(e, "clone");
                if (!op.IsClone && (op.Name != null || op.Levels != null || op.CooldownGroup != null)) throw Bad(e, "name, levels and cooldown-group belong to a clone (clone=\"shipped id\")");
                if (op.CooldownGroup != null && op.CooldownGroup != "own" && !System.Text.RegularExpressions.Regex.IsMatch(op.CooldownGroup, "^[0-9]{1,10}$"))
                    throw Bad(e, "cooldown-group=\"" + op.CooldownGroup + "\": \"own\" or a group id (0 = none)");
                if (op.CooldownGroup != null && kind != SkillKind.Skill) throw Bad(e, "cooldown-group belongs to a skill");
                if (op.IsClone && op.Clone == op.Id) throw Bad(e, "a skill cannot be a clone of itself");
                foreach (XmlElem c in e.Children)
                {
                    switch (c.Name)
                    {
                        case "level":
                            {
                                var s = new SkillSet { Scope = SkillScope.Level, Levels = LevelsOf(c, "value"), Line = c.Line };
                                Fields(c, s, kind, new[] { "value" });
                                op.Sets.Add(s);
                                break;
                            }
                        case "motion":
                            {
                                if (kind != SkillKind.Skill) throw Bad(c, "an effect has no motions");
                                var s = new SkillSet { Scope = SkillScope.Motion, Levels = LevelsOf(c, "level"), Motion = Index(c, "index", this, true), Line = c.Line };
                                Fields(c, s, kind, new[] { "level", "index" });
                                op.Sets.Add(s);
                                break;
                            }
                        case "attack":
                            {
                                if (kind != SkillKind.Skill) throw Bad(c, "an effect has no attacks");
                                var s = new SkillSet { Scope = SkillScope.Attack, Levels = LevelsOf(c, "level"), Motion = Index(c, "motion", this, true), Attack = Index(c, "index", this, true), Line = c.Line };
                                Fields(c, s, kind, new[] { "level", "motion", "index" });
                                op.Sets.Add(s);
                                break;
                            }
                        case "tooltip":
                            {
                                Only(c, "level", "kms-param", "gms-text", "lang");
                                var t = new SkillText { Levels = LevelsOf(c, "level"), KmsParam = c.Attr("kms-param"), GmsText = c.Attr("gms-text"), Line = c.Line };
                                if (c.Attr("lang") != null)
                                {
                                    t.Lang = c.Attr("lang");
                                    if (t.Lang.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(t.Lang, "^[a-z]+$")) throw Bad(c, "<tooltip lang=\"" + t.Lang + "\">: a language folder name such as en, de or pt");
                                }
                                if (t.KmsParam == null && t.GmsText == null) throw Bad(c, "<tooltip> needs kms-param or gms-text");
                                op.Texts.Add(t);
                                break;
                            }
                        case "raw":
                            {
                                Only(c, "side", "level", "at");
                                string side = Need(c, "side");
                                Profile p = Profile.Find(side);
                                if (p == null) throw Bad(c, "side=\"" + side + "\" is not kms-client, gms-client or server");
                                var r = new SkillRaw { Side = p.Name, Levels = LevelsOf(c, "level"), At = c.Attr("at") ?? "", Line = c.Line };
                                try { RowPath.Parse(r.At); } catch (ModKitException x) { throw Bad(c, x.Message); }
                                var tmp = new RowOp();
                                ReadAttrOps(c, tmp, false);
                                foreach (AttrOp a in tmp.Attrs) { if (a.Kind == AttrOpKind.Text) throw Bad(c, "<raw> takes <set> and <unset>"); r.Attrs.Add(a); }
                                if (r.Attrs.Count == 0) throw Bad(c, "<raw> without <set> or <unset>");
                                op.Raws.Add(r);
                                break;
                            }
                        case "swap":
                            {
                                if (kind != SkillKind.Skill) throw Bad(c, "an effect has no motions");
                                Only(c, "level", "motion", "from-skill", "from-level", "from-motion");
                                op.Swaps.Add(new SkillSwap
                                {
                                    Levels = LevelsOf(c, "level"), Motion = Index(c, "motion", this, true), FromSkill = Id(c, "from-skill"),
                                    FromLevel = Index(c, "from-level", this, true), FromMotion = Index(c, "from-motion", this, true), Line = c.Line,
                                });
                                break;
                            }
                        case "own":
                            {
                                if (!op.IsClone || kind != SkillKind.Skill) throw Bad(c, "<own> belongs to a skill clone");
                                Only(c, "effect", "as");
                                op.Own.Add(new KeyValuePair<long, long>(Id(c, "effect"), Id(c, "as")));
                                break;
                            }
                        default: throw Bad(c, "unknown element <" + c.Name + "> in <" + e.Name + "> (known: level, motion, attack, tooltip, raw, swap, own)");
                    }
                }
                if (!op.IsClone && op.Sets.Count + op.Raws.Count + op.Texts.Count + op.Swaps.Count == 0) throw Bad(e, "<" + e.Name + "> changes nothing");
                return op;
            }

            internal JobSkillOp ReadJobSkill(XmlElem e)
            {
                Only(e, "job", "skill", "replace", "tier", "position", "max-level", "learn-level", "require-skills", "require-levels");
                var j = new JobSkillOp { Job = (int)Num(e, "job"), Skill = Id(e, "skill"), Line = e.Line, MaxLevel = e.Attr("max-level"), LearnLevel = e.Attr("learn-level"), RequireSkills = e.Attr("require-skills"), RequireLevels = e.Attr("require-levels") };
                if (e.Attr("replace") != null) j.Replace = Id(e, "replace");
                if (e.Attr("tier") != null) { j.Tier = (int)Num(e, "tier"); if (j.Tier != 1 && j.Tier != 2) throw Bad(e, "tier is 1 or 2"); }
                j.Position = e.Attr("position");
                if ((j.Replace >= 0) == (j.Position != null)) throw Bad(e, "<jobskill> needs either replace=\"skill id\" or position=\"x,y\"");
                if (j.Position != null && SkillJobs.ParsePos(j.Position) == null) throw Bad(e, "position=\"" + j.Position + "\" is not \"x,y\"");
                if (j.Replace >= 0 && e.Attr("tier") != null) throw Bad(e, "tier belongs to a new row (position=); a replaced slot keeps its tier");
                if (e.Children.Count > 0) throw Bad(e, "<jobskill> has no child elements");
                return j;
            }
        }

        void WriteSkill(StringBuilder sb, SkillOp op)
        {
            sb.Append("  <").Append(op.Tag); A(sb, "id", op.Id.ToString(CultureInfo.InvariantCulture));
            if (op.IsClone) A(sb, "clone", op.Clone.ToString(CultureInfo.InvariantCulture));
            A(sb, "levels", op.Levels); A(sb, "name", op.Name); A(sb, "cooldown-group", op.CooldownGroup);
            sb.Append(">\n");
            foreach (KeyValuePair<long, long> o in op.Own)
            { sb.Append("    <own"); A(sb, "effect", o.Key.ToString(CultureInfo.InvariantCulture)); A(sb, "as", o.Value.ToString(CultureInfo.InvariantCulture)); sb.Append(" />\n"); }
            foreach (SkillSet s in op.Sets)
            {
                if (s.Scope == SkillScope.Level) { sb.Append("    <level"); A(sb, "value", s.Levels); }
                else if (s.Scope == SkillScope.Motion) { sb.Append("    <motion"); A(sb, "level", s.Levels); A(sb, "index", s.Motion.ToString(CultureInfo.InvariantCulture)); }
                else { sb.Append("    <attack"); A(sb, "level", s.Levels); A(sb, "motion", s.Motion.ToString(CultureInfo.InvariantCulture)); A(sb, "index", s.Attack.ToString(CultureInfo.InvariantCulture)); }
                foreach (KeyValuePair<string, string> f in s.Fields) A(sb, f.Key, f.Value);
                sb.Append(" />\n");
            }
            foreach (SkillText t in op.Texts) { sb.Append("    <tooltip"); A(sb, "level", t.Levels); A(sb, "kms-param", t.KmsParam); A(sb, "gms-text", t.GmsText); A(sb, "lang", t.Lang == "en" ? null : t.Lang); sb.Append(" />\n"); }
            foreach (SkillSwap w in op.Swaps)
            {
                sb.Append("    <swap"); A(sb, "level", w.Levels); A(sb, "motion", w.Motion.ToString(CultureInfo.InvariantCulture)); A(sb, "from-skill", w.FromSkill.ToString(CultureInfo.InvariantCulture));
                A(sb, "from-level", w.FromLevel.ToString(CultureInfo.InvariantCulture)); A(sb, "from-motion", w.FromMotion.ToString(CultureInfo.InvariantCulture)); sb.Append(" />\n");
            }
            foreach (SkillRaw r in op.Raws)
            {
                sb.Append("    <raw"); A(sb, "side", r.Side); A(sb, "level", r.Levels); if (r.At.Length > 0) A(sb, "at", r.At); sb.Append(">\n");
                WriteSets(sb, "      ", r.Attrs);
                sb.Append("    </raw>\n");
            }
            sb.Append("  </").Append(op.Tag).Append(">\n");
        }

        void WriteJobSkill(StringBuilder sb, JobSkillOp j)
        {
            sb.Append("  <jobskill"); A(sb, "job", j.Job.ToString(CultureInfo.InvariantCulture)); A(sb, "skill", j.Skill.ToString(CultureInfo.InvariantCulture));
            if (j.Replace >= 0) A(sb, "replace", j.Replace.ToString(CultureInfo.InvariantCulture));
            else { if (j.Tier != 1) A(sb, "tier", j.Tier.ToString(CultureInfo.InvariantCulture)); A(sb, "position", j.Position); }
            A(sb, "max-level", j.MaxLevel); A(sb, "learn-level", j.LearnLevel); A(sb, "require-skills", j.RequireSkills); A(sb, "require-levels", j.RequireLevels);
            sb.Append(" />\n");
        }

        void WriteSkills(StringBuilder sb)
        {
            foreach (SkillOp s in Skills) WriteSkill(sb, s);
            foreach (JobSkillOp j in JobSkills) WriteJobSkill(sb, j);
        }
    }

    /// <summary>Level lists: "*" (or empty) = all, "1", "1,3", "2-4".</summary>
    public static class SkillLevels
    {
        /// <summary>The levels, or null for all.</summary>
        public static List<int> Parse(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0 || t == "*") return null;
            var l = new List<int>();
            foreach (string part in t.Split(','))
            {
                string p = part.Trim();
                int dash = p.IndexOf('-');
                int a, b;
                if (dash > 0)
                {
                    if (!int.TryParse(p.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out a) || !int.TryParse(p.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out b) || b < a)
                        throw new ModKitException("levels \"" + text + "\": \"" + p + "\" is not a range a-b");
                    for (int i = a; i <= b; i++) if (!l.Contains(i)) l.Add(i);
                }
                else
                {
                    if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out a)) throw new ModKitException("levels \"" + text + "\": \"" + p + "\" is not a level");
                    if (!l.Contains(a)) l.Add(a);
                }
            }
            return l;
        }

        public static bool Has(string text, int level)
        {
            List<int> l = Parse(text);
            return l == null || l.Contains(level);
        }
    }

    /// <summary>Field values: a decimal number in neutral units, or "*k" (the shipped value times k).</summary>
    public static class SkillValue
    {
        public static bool Valid(string v)
        {
            decimal d;
            string s = (v ?? "").Trim();
            if (s.StartsWith("*", StringComparison.Ordinal)) s = s.Substring(1);
            return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        public static bool TryNum(string v, out decimal d)
        {
            return decimal.TryParse((v ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>The shortest invariant text of a number ("3", "10.28", "0.5").</summary>
        public static string Text(decimal d)
        {
            string s = d.ToString("0.############################", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        /// <summary>
        /// The side's new attribute text: "3" s with scale 1000 -> "3000"; "*2" of "5.14" -> "10.28". Null with a
        /// reason when the value cannot be made (a factor on a missing or non-number value).
        /// </summary>
        public static string Convert(string mod, decimal scale, bool whole, string shipped, out string why)
        {
            why = null;
            string s = mod.Trim();
            decimal d;
            if (s.StartsWith("*", StringComparison.Ordinal))
            {
                decimal k, old;
                TryNum(s.Substring(1), out k);
                if (shipped == null) { why = "the shipped row has no value to multiply"; return null; }
                if (!TryNum(shipped, out old)) { why = "the shipped value \"" + shipped + "\" is not a number"; return null; }
                d = old * k;
            }
            else
            {
                TryNum(s, out d);
                d *= scale;
            }
            if (whole && d != decimal.Truncate(d))
            {
                why = "the side keeps whole numbers and " + Text(d) + " is not one";
                d = decimal.Round(d, MidpointRounding.AwayFromZero);
                why = null;
            }
            return Text(d);
        }

        /// <summary>A side's value in neutral units ("8000" ms -> "8").</summary>
        public static string Neutral(string shipped, decimal scale)
        {
            decimal d;
            if (shipped == null || !TryNum(shipped, out d)) return shipped;
            return Text(d / scale);
        }
    }

    // ---- the field map: where each field sits on each side ----

    /// <summary>One side of a skill entity: where its rows and levels are.</summary>
    public sealed class SkillSide
    {
        public bool Bundle;
        /// <summary>Entry pattern: "skilldata/*.xml" (bundle) or "skill/{id/1000000}/{id}.xml".</summary>
        public string Entry;
        /// <summary>Bundle: the row element and its id attribute.</summary>
        public string Row, Key;
        /// <summary>The level element (a child of the row) and where its number is ("@value", "BasicProperty@level").</summary>
        public string Level, LevelKey;
        /// <summary>Bundle: the folder new rows go to, one file per band of 100000 ids ("skilldata/{id/100000}.xml").</summary>
        public string NewEntry;
        public string Motion, Attack;

        /// <summary>The entry of an id (files side): {id} and {id/n}.</summary>
        public string EntryOf(long id) { return SkillMap.Fill(Bundle ? NewEntry : Entry, id); }
    }

    public sealed class SkillField
    {
        public string Name;
        public SkillScope Scope;
        public string Unit = "";
        /// <summary>A change of this field makes the tooltip show an old number.</summary>
        public bool Tooltip;
        /// <summary>Per side: path under the scope element ("beginCondition") and attribute; side value = neutral * scale.</summary>
        public string BundleAt, BundleAttr, FilesAt, FilesAttr;
        public decimal BundleScale = 1, FilesScale = 1;
        public bool BundleWhole, FilesWhole;
        /// <summary>
        /// The value the game uses when the attribute is left out (neutral units), or null when none is known. A "*k"
        /// on a missing attribute multiplies this, and setting the field writes the attribute.
        /// </summary>
        public string Default;

        public string At(bool bundle) { return bundle ? BundleAt : FilesAt; }
        public string Attr(bool bundle) { return bundle ? BundleAttr : FilesAttr; }
        public decimal Scale(bool bundle) { return bundle ? BundleScale : FilesScale; }
        public bool Whole(bool bundle) { return bundle ? BundleWhole : FilesWhole; }
    }

    /// <summary>An attribute that names a skill or effect id ("additionalID" names an effect).</summary>
    public sealed class SkillRef
    {
        public bool Bundle;
        /// <summary>The element it is on, or null for any element.</summary>
        public string Element;
        /// <summary>The attribute name, or its ending (case is ignored) when Suffix.</summary>
        public string Attr;
        public bool Suffix;
        /// <summary>"skill", "effect" or "either".</summary>
        public string To;

        public bool Matches(string element, string attr)
        {
            if (Element != null && Element != element) return false;
            return Suffix ? attr.EndsWith(Attr, StringComparison.OrdinalIgnoreCase) : attr == Attr;
        }
    }

    /// <summary>A string file of an entity: the name or description rows of an id.</summary>
    public sealed class SkillString
    {
        public string Role;         // "name", "text"
        public bool Bundle;
        /// <summary>The file name pattern under string/&lt;lang&gt;/ ("skillname*.xml").</summary>
        public string Files;
        /// <summary>Where new rows go ("skillname.xml"); null = the file the source row is in.</summary>
        public string NewFile;
        /// <summary>The rows have a level attribute.</summary>
        public bool Leveled;
        /// <summary>The attribute with the text ("name", "uiDescription").</summary>
        public string Attr;
    }

    public sealed class SkillEntityMap
    {
        public string Name;
        public SkillKind Kind;
        public SkillSide BundleSide, FilesSide;
        public readonly List<SkillField> Fields = new List<SkillField>();
        public readonly List<SkillRef> Refs = new List<SkillRef>();
        public readonly List<SkillString> Strings = new List<SkillString>();

        public SkillSide Side(bool bundle) { return bundle ? BundleSide : FilesSide; }

        public SkillField Field(string name)
        {
            foreach (SkillField f in Fields) if (f.Name == name) return f;
            return null;
        }

        public string[] FieldNames(SkillScope scope)
        {
            var l = new List<string>();
            foreach (SkillField f in Fields) if (f.Scope == scope) l.Add(f.Name);
            return l.ToArray();
        }

        /// <summary>The table an attribute names ("skill", "effect", "either"), or null.</summary>
        public string RefTo(bool bundle, string element, string attr)
        {
            foreach (SkillRef r in Refs) if (r.Bundle == bundle && r.Matches(element, attr)) return r.To;
            return null;
        }
    }

    public sealed class SkillMap
    {
        public SkillEntityMap Skill, Effect;
        public SkillEntityMap Entity(SkillKind k) { return k == SkillKind.Skill ? Skill : Effect; }

        static SkillMap def;
        static readonly object gate = new object();

        /// <summary>The map inside the library (Maps/skill.skillmap.xml).</summary>
        public static SkillMap Default
        {
            get
            {
                lock (gate)
                {
                    if (def != null) return def;
                    using (Stream s = typeof(SkillMap).Assembly.GetManifestResourceStream("MS2.ModKit.skill.skillmap.xml"))
                    {
                        if (s == null) throw new ModKitException("the library has no skill.skillmap.xml");
                        var ms = new MemoryStream();
                        var buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        def = Parse(ms.ToArray(), "skill.skillmap.xml");
                    }
                    return def;
                }
            }
        }

        /// <summary>"{id}" and "{id/n}" of a pattern.</summary>
        public static string Fill(string pattern, long id)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < pattern.Length)
            {
                int o = pattern.IndexOf("{id", i, StringComparison.Ordinal);
                if (o < 0) { sb.Append(pattern, i, pattern.Length - i); break; }
                sb.Append(pattern, i, o - i);
                int c = pattern.IndexOf('}', o);
                string inner = pattern.Substring(o + 1, c - o - 1);
                long v = id;
                if (inner.StartsWith("id/", StringComparison.Ordinal)) v = id / long.Parse(inner.Substring(3), CultureInfo.InvariantCulture);
                sb.Append(v.ToString(CultureInfo.InvariantCulture));
                i = c + 1;
            }
            return sb.ToString();
        }

        static string Need(XmlElem e, string n, string label)
        {
            string v = e.Attr(n);
            if (v == null) throw new ModKitException(label + " line " + e.Line + ": <" + e.Name + "> needs " + n);
            return v;
        }

        static void Split(string path, out string at, out string attr)
        {
            int a = path.LastIndexOf('@');
            at = a < 0 ? path : path.Substring(0, a);
            attr = a < 0 ? null : path.Substring(a + 1);
        }

        public static SkillMap Parse(byte[] bytes, string label)
        {
            XmlDoc doc = XmlDoc.Parse(bytes, label);
            var map = new SkillMap();
            foreach (XmlElem en in doc.Root.Children)
            {
                if (en.Name != "entity") continue;
                var m = new SkillEntityMap { Name = Need(en, "name", label) };
                m.Kind = m.Name == "skill" ? SkillKind.Skill : SkillKind.Effect;
                foreach (XmlElem c in en.Children)
                {
                    switch (c.Name)
                    {
                        case "bundle":
                        case "files":
                            {
                                var s = new SkillSide
                                {
                                    Bundle = c.Name == "bundle", Entry = Need(c, "entry", label), Row = c.Attr("row"), Key = c.Attr("key") ?? "id",
                                    Level = Need(c, "level", label), LevelKey = Need(c, "level-key", label), NewEntry = c.Attr("new-entry"),
                                    Motion = c.Attr("motion"), Attack = c.Attr("attack"),
                                };
                                if (s.Bundle) m.BundleSide = s; else m.FilesSide = s;
                                break;
                            }
                        case "field":
                            {
                                var f = new SkillField { Name = Need(c, "name", label), Unit = c.Attr("unit") ?? "", Tooltip = c.Attr("tooltip") == "true" };
                                string sc = c.Attr("scope") ?? "level";
                                f.Scope = sc == "motion" ? SkillScope.Motion : sc == "attack" ? SkillScope.Attack : SkillScope.Level;
                                Split(Need(c, "bundle", label), out f.BundleAt, out f.BundleAttr);
                                Split(Need(c, "files", label), out f.FilesAt, out f.FilesAttr);
                                if (c.Attr("bundle-scale") != null) f.BundleScale = decimal.Parse(c.Attr("bundle-scale"), CultureInfo.InvariantCulture);
                                if (c.Attr("files-scale") != null) f.FilesScale = decimal.Parse(c.Attr("files-scale"), CultureInfo.InvariantCulture);
                                f.BundleWhole = c.Attr("bundle-whole") == "true";
                                f.FilesWhole = c.Attr("files-whole") == "true";
                                f.Default = c.Attr("default");
                                if (f.Default != null && !SkillValue.Valid(f.Default)) throw new ModKitException(label + ": field " + f.Name + " default=\"" + f.Default + "\" is not a number");
                                m.Fields.Add(f);
                                break;
                            }
                        case "ref":
                            {
                                var r = new SkillRef { Bundle = Need(c, "side", label) == "bundle", Element = c.Attr("element"), To = Need(c, "to", label) };
                                if (c.Attr("suffix") != null) { r.Attr = c.Attr("suffix"); r.Suffix = true; }
                                else r.Attr = Need(c, "attr", label);
                                m.Refs.Add(r);
                                break;
                            }
                        case "string":
                            m.Strings.Add(new SkillString
                            {
                                Role = Need(c, "role", label), Bundle = Need(c, "side", label) == "bundle", Files = Need(c, "files", label), NewFile = c.Attr("new-file"),
                                Leveled = c.Attr("leveled") == "true", Attr = Need(c, "attr", label),
                            });
                            break;
                        default: throw new ModKitException(label + " line " + c.Line + ": unknown <" + c.Name + ">");
                    }
                }
                if (m.BundleSide == null || m.FilesSide == null) throw new ModKitException(label + ": entity " + m.Name + " needs <bundle> and <files>");
                if (m.Kind == SkillKind.Skill) map.Skill = m; else map.Effect = m;
            }
            if (map.Skill == null || map.Effect == null) throw new ModKitException(label + ": needs the skill and effect entities");
            return map;
        }
    }

    /// <summary>
    /// The feature gate of one data root: a row with feature="X" is read when X's level for the locale (table/feature.xml)
    /// is at or below the locale's Live level (table/feature_setting.xml). kms-client reads locale KR, gms-client and
    /// server NA.
    /// </summary>
    public sealed class FeatureGate
    {
        public readonly string Locale;
        public readonly int Level;
        readonly Dictionary<string, int> features = new Dictionary<string, int>(StringComparer.Ordinal);

        public FeatureGate(string locale, int level, IDictionary<string, int> featureLevels)
        {
            Locale = locale; Level = level;
            if (featureLevels != null) foreach (KeyValuePair<string, int> kv in featureLevels) features[kv.Key] = kv.Value;
        }

        public static string LocaleOf(Profile p) { return p == Profile.KmsClient ? "KR" : "NA"; }

        /// <summary>The gate of an archive (its table/feature.xml and table/feature_setting.xml); every feature is off when they are missing.</summary>
        public static FeatureGate Of(M2dArchive arch, string locale)
        {
            int level = -1;
            var levels = new Dictionary<string, int>();
            M2dEntry st = arch.Find("table/feature_setting.xml"), fe = arch.Find("table/feature.xml");
            if (st != null)
                foreach (XmlElem e in XmlDoc.Parse(arch.Read(st), "feature_setting.xml").Root.Children)
                    if (e.Attr("type") == "Live") int.TryParse(e.Attr(locale) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
            if (fe != null)
                foreach (XmlElem e in XmlDoc.Parse(arch.Read(fe), "feature.xml").Root.Children)
                {
                    int v;
                    string n = e.Attr("name");
                    if (n != null && int.TryParse(e.Attr(locale) ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) levels[n] = v;
                }
            return new FeatureGate(locale, level, levels);
        }

        /// <summary>The feature is on (an empty name is always on).</summary>
        public bool On(string feature)
        {
            if (string.IsNullOrEmpty(feature)) return true;
            int v;
            return features.TryGetValue(feature, out v) && v <= Level;
        }

        int Rank(string feature)
        {
            int v;
            return string.IsNullOrEmpty(feature) || !features.TryGetValue(feature, out v) ? 0 : v;
        }

        /// <summary>
        /// The variant the game reads among elements that are one row: drop those whose feature is off or whose locale
        /// is another one, then take the highest feature level, then the own locale before an empty one. Null when none is read.
        /// </summary>
        public XmlElem Pick(IList<XmlElem> variants)
        {
            XmlElem best = null;
            int bestRank = -1, bestLoc = -1;
            foreach (XmlElem e in variants)
            {
                string loc = e.Attr("locale") ?? "";
                if (loc.Length > 0 && !string.Equals(loc, Locale, StringComparison.OrdinalIgnoreCase)) continue;
                string f = e.Attr("feature") ?? "";
                if (!On(f)) continue;
                int r = Rank(f), l = loc.Length > 0 ? 1 : 0;
                if (r > bestRank || (r == bestRank && l > bestLoc)) { best = e; bestRank = r; bestLoc = l; }
            }
            return best;
        }
    }
}
