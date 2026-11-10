// The npc editor: the entity editor (Entities.cs) for npcs.
//
// What makes up an npc:
//   client:Xml.m2d      npcdata/NNN.xml                   <npc id> rows; NNN is the id divided by 100000. The client
//   (kms-client)                                          reads an id from the file of its own block only, and has
//                                                         no index table of npcs.
//   server:Xml.m2d,     npc/<aa>/<bb>/<id>.xml            one file per npc, the 8-digit id is the file name
//   gms-client:Xml.m2d
//   every target        string/<lang>/npcname.xml         <key id name>     the name, one file per language; the
//                       string/<lang>/npcnameplural.xml   <key id name>     id is written with 8 digits, and an id
//                       string/<lang>/npctitle.xml        <key id name>     can have one row per feature
//                       string/<lang>/npcfunctionname.xml <key id name>     the label of what the npc does
//                       emotion/npc/<id>.xml              the npc's face animations (about a third of the npcs)
//   server, gms-client  script/npc/<id>.xml               the npc's talk script (npcs that talk)
// A new id needs nothing else: map spawn lists, finder lists and quest tables name npcs that a feature uses, not
// npcs that exist. A cloned npc is placed by the server's spawn command or by such a table.
//
// Field names are the field map's client side (Maps/npc.fieldmap.xml): "@level" is an attribute of the client's
// <environment>, "stat@hp" of its <stat>. Without a kms-client the same names reach the server's fields through the map.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS2.ModKit
{
    public sealed class NpcDraft : EntityDraft
    {
        internal NpcDraft(NpcEditor editor, long id) : base(editor, id) { }

        public new NpcEditor Editor { get { return (NpcEditor)base.Editor; } }

        /// <summary>The title shown under the name, in the editor's language (null: none).</summary>
        public string Title { get { return First("title", "name"); } }

        /// <summary>
        /// Sets the title in every language file of every target. A language that has a title file but no title row
        /// of the npc gets one (shipped npcs often have title rows in a few languages only). An empty title adds no
        /// rows. Returns the number of rows.
        /// </summary>
        public int SetTitle(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.Length > 0 && Editor.AddStringRows(2, "<key id=\"" + NpcEditor.Padded(Id) + "\" name=\"\" />", this) > 0)
                Editor.Fill(this);
            return SetAll("title", "name", value);
        }

        /// <summary>The level (the client's @level, else the server's basic@level).</summary>
        public string Level { get { return GetField("@level"); } }

        /// <summary>
        /// Sets the level on both sides. The server's basic@difficulty follows where it equalled the level (as most
        /// shipped npcs have it; the others keep theirs, mostly 0). Returns the number of sections set.
        /// </summary>
        public int SetLevel(int level)
        {
            if (level < 1) throw new ModKitException("the level must be 1 or more");
            string v = level.ToString(CultureInfo.InvariantCulture);
            int n = 0;
            foreach (EntitySection s in Sections.ToArray())
            {
                if (s.Part.Kind != EntityEditor.FileKind || BelowEnv(s.Chain) != "basic") continue;
                if (Get(s, "difficulty") != null && Get(s, "difficulty") == Get(s, "level")) { Set(s, "difficulty", v); n++; }
            }
            return SetBoth("@level", v) + n;
        }

        /// <summary>The hit points (stat@hp).</summary>
        public string Hp { get { return GetField("stat@hp"); } }

        /// <summary>Sets the hit points on both sides.</summary>
        public int SetHp(long hp)
        {
            if (hp < 1) throw new ModKitException("the hp must be 1 or more");
            return SetBoth("stat@hp", hp.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The model (model@kfm): the name of the model file the npc is drawn with.</summary>
        public string Model { get { return GetField("model@kfm"); } }

        /// <summary>Sets the model on both sides. The model has to exist in the client's model data.</summary>
        public int SetModel(string kfm)
        {
            if (string.IsNullOrEmpty(kfm)) throw new ModKitException("the model name is empty");
            return SetBoth("model@kfm", kfm);
        }
    }

    public sealed class NpcEditor : EntityEditor
    {
        public const string IdSpace = "npc";

        public NpcEditor(DataSet data, Mod mod)
            : base(IdSpace, data, mod, new[] { "npcname.xml", "npcnameplural.xml", "npctitle.xml", "npcfunctionname.xml" }, new[] { "name", "plural", "title", "function" })
        {
        }

        /// <summary>An npc id as the string files and file names write it: 8 digits.</summary>
        public static string Padded(long id) { return id.ToString("00000000", CultureInfo.InvariantCulture); }

        protected override EntityDraft NewDraft(long id) { return new NpcDraft(this, id); }

        protected override string[] CompanionFiles(long id)
        {
            return new[] { "emotion/npc/" + Padded(id) + ".xml", "script/npc/" + Padded(id) + ".xml" };
        }

        /// <summary>The npc of an id, as the original data and the mod have it.</summary>
        public NpcDraft Open(long id) { return (NpcDraft)OpenAny(id); }

        /// <summary>
        /// The fields of the npc table the field map pairs between the kms-client and the server (client side names,
        /// "@level", "stat@hp", ...), the ones a form shows. Without a map: the level, hp and model.
        /// </summary>
        public List<string> MappedFields()
        {
            var list = new List<string>();
            EntityMap em = Maps == null ? null : Maps.Entity(Entity);
            if (em != null) foreach (FieldMapEntry e in em.Entries) if (!list.Contains(e.Client)) list.Add(e.Client);
            if (list.Count == 0) list.AddRange(new[] { "@level", "stat@hp", "model@kfm" });
            return list;
        }

        /// <summary>
        /// A new npc that starts as a copy of a shipped one (EntityEditor.CloneAny): its npc row, its string rows in
        /// every language of every target, its per-id file and its emotion and script files where the source has
        /// them. name may be null to keep the source's; level and hp 0 to keep the source's.
        /// </summary>
        public NpcDraft Clone(long sourceId, long newId, string name, int level, long hp)
        {
            var d = (NpcDraft)CloneAny(sourceId, newId);
            if (name != null) d.SetName(name);
            if (level > 0) d.SetLevel(level);
            if (hp > 0) d.SetHp(hp);
            return d;
        }

        /// <summary>
        /// The places a new npc id is written to, per target, as text lines: for the plan view and the README.
        /// </summary>
        public List<string> Registration(long id)
        {
            var l = new List<string>();
            if (BundleXml != null) l.Add(BundleXml + "  " + BundleEntry(id) + "  <npc id=\"" + id + "\"> row");
            if (FileXml != null) l.Add(FileXml + "  " + NewFileEntry(id));
            foreach (string archive in Sides())
                foreach (string f in StringFiles)
                {
                    List<string> langs = LanguagesIn(archive, f);
                    if (langs.Count > 0) l.Add(archive + "  string/<" + string.Join(",", langs.ToArray()) + ">/" + f + "  <key id=\"" + Padded(id) + "\"> row, where the source has one");
                }
            foreach (string archive in Sides())
                foreach (string c in CompanionFiles(id))
                    l.Add(archive + "  " + c + ", where the source has one");
            if (FileXml != null && FileXml.StartsWith("server:", StringComparison.Ordinal))
            {
                string gms = "gms-client:" + FileXml.Substring("server:".Length);
                if (Data.Exists(gms))
                    l.Add(gms + "  the server's lines above, mirrored: the same files, rows and attributes, without the server-only ones and the languages it does not ship");
            }
            return l;
        }
    }
}
