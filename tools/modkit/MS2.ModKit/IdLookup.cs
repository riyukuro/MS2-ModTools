// The id lists behind the plugin's id pickers: one search (id or name) over the npcs, items, skills, effects, quests,
// shops and drop boxes of an editor session, through the same Find each editor's own view uses, and over the maps,
// interact objects and dungeons (IdLists.cs); the ids the mod adds first. And which kind of id a field or a quest condition code holds.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS2.ModKit
{
    public sealed class IdLookup
    {
        public const string Npc = "npc", Item = "item", Skill = "skill", Effect = "effect", Quest = "quest", Shop = "shop", DropBox = "drop box",
            Map = "map", InteractObject = "interact object", Dungeon = "dungeon";
        public static readonly string[] Kinds = { Npc, Item, Skill, Effect, Quest, Shop, DropBox, Map, InteractObject, Dungeon };

        readonly EditorSession session;
        ItemEditor items;
        NpcEditor npcs;
        QuestEditor quests;
        SkillEditor skills;
        GroupEditor groups;
        TableIdList maps, interacts, dungeons;

        /// <summary>Over the data and the mod of a session; ask for a new one after the mod is replaced.</summary>
        public IdLookup(EditorSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            this.session = session;
        }

        public Mod Mod { get { return session.Mod; } }
        public DataSet Data { get { return session.Data; } }

        /// <summary>
        /// The ids of a kind whose id starts with the text or whose name contains it (shops and drop boxes: the id
        /// only), at most max; the ids the mod adds first. An empty text gives the first ids.
        /// </summary>
        public List<EntityHit> Find(string kind, string text, int max)
        {
            text = (text ?? "").Trim();
            switch (kind)
            {
                case Npc: return (npcs ?? (npcs = session.Npcs())).Find(text, max);
                case Item: return (items ?? (items = session.Items())).Find(text, max);
                case Quest:
                    {
                        var l = new List<EntityHit>();
                        QuestEditor q = quests ?? (quests = session.Quests());
                        if (!q.HasData) return l;
                        foreach (QuestHit h in q.Find(text, max)) l.Add(new EntityHit { Id = h.Id, Name = h.Name ?? "", Added = h.Added });
                        return l;
                    }
                case Skill: return SkillHits(SkillKind.Skill, text, max);
                case Effect: return SkillHits(SkillKind.Effect, text, max);
                case Shop: return (groups ?? (groups = session.Groups())).Find(GroupKind.Shop, text, max);
                case DropBox: return (groups ?? (groups = session.Groups())).Find(GroupKind.DropBox, text, max);
                case Map: return (maps ?? (maps = TableIdList.Maps(Data, Mod))).Find(text, max);
                case InteractObject: return (interacts ?? (interacts = TableIdList.InteractObjects(Data, Mod))).Find(text, max);
                case Dungeon: return (dungeons ?? (dungeons = TableIdList.Dungeons(Data, Mod))).Find(text, max);
            }
            throw new ModKitException("no id list \"" + kind + "\" (" + string.Join(", ", Kinds) + ")");
        }

        List<EntityHit> SkillHits(SkillKind kind, string text, int max)
        {
            SkillEditor ed = skills ?? (skills = session.Skills());
            var l = new List<EntityHit>();
            var seen = new HashSet<long>();
            // the mod's clones are not shipped: they come first, under the source's name
            foreach (SkillOp op in Mod.Skills)
            {
                if (op.Kind != kind || !op.IsClone || l.Count >= max) continue;
                string id = op.Id.ToString(CultureInfo.InvariantCulture), name = op.Name ?? "";
                if (string.IsNullOrEmpty(op.Name))
                {
                    SkillInfo src = ed.Open(kind, op.Clone);
                    if (src != null) name = src.Name ?? "";
                }
                if (text.Length > 0 && !id.StartsWith(text, StringComparison.Ordinal) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (seen.Add(op.Id)) l.Add(new EntityHit { Id = op.Id, Name = name, Added = true });
            }
            foreach (KeyValuePair<long, string> kv in ed.Find(kind, text, max))
            {
                if (l.Count >= max) break;
                if (seen.Add(kv.Key)) l.Add(new EntityHit { Id = kv.Key, Name = kv.Value ?? "" });
            }
            return l;
        }

        /// <summary>
        /// The kind of id the code of a quest condition of this type is (QuestDraft.CodeTable), or null when it is
        /// free text. A buff's code is an additional effect.
        /// </summary>
        public static string QuestCodeKind(string conditionType)
        {
            if (string.Equals(conditionType, "buff", StringComparison.OrdinalIgnoreCase)) return Effect;
            string t = QuestDraft.CodeTable(conditionType);
            return t == Npc || t == Item || t == Quest || t == Skill || t == Map || t == InteractObject || t == Dungeon ? t : null;
        }

        /// <summary>
        /// The kind of id an npc field holds ("@shopId", "dropiteminfo@globalDropBoxId", or the attribute alone),
        /// or null.
        /// </summary>
        public static string NpcFieldKind(string field)
        {
            if (string.IsNullOrEmpty(field)) return null;
            int at = field.LastIndexOf('@');
            string a = at >= 0 ? field.Substring(at + 1) : field;
            if (string.Equals(a, "shopId", StringComparison.OrdinalIgnoreCase)) return Shop;
            if (a.EndsWith("DropBoxId", StringComparison.OrdinalIgnoreCase)) return DropBox;
            return null;
        }

        /// <summary>The kind of id a row field of a shop or drop box holds (the item of the row), or null.</summary>
        public static string GroupRowFieldKind(GroupKind kind, string field)
        {
            if (kind == GroupKind.Shop) return field == "id" ? Item : null;
            return field == "itemID" || field == "itemID2" || field == "item" ? Item : null;
        }
    }
}
