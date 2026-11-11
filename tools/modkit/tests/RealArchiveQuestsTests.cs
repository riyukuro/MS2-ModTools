using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    /// <summary>
    /// The quest editor over real data (MS2_CLIENT_DATA, MS2_GMS_CLIENT_DATA, MS2_SERVER_DATA; skipped when none is
    /// set): a spread of the quests is opened, edited and cloned, and the plan has no error and no conflict.
    /// </summary>
    [TestFixture]
    public class RealArchiveQuestsTests
    {
        [Test]
        public void EditAndCloneASpreadOfQuests()
        {
            string k = RealData.Client, g = Environment.GetEnvironmentVariable("MS2_GMS_CLIENT_DATA"), s = RealData.Server;
            if (string.IsNullOrEmpty(k) && string.IsNullOrEmpty(g) && string.IsNullOrEmpty(s)) Assert.Ignore("no data folder is set");
            using (var data = new DataSet(k, g, s))
            {
                var mod = new Mod { Id = "real-quests", Name = "real-quests" };
                var ed = new QuestEditor(data, mod);
                Assert.That(ed.HasData, Is.True);
                List<QuestHit> all = ed.Find("", int.MaxValue);
                Assert.That(all.Count, Is.GreaterThan(100));
                int step = Math.Max(1, all.Count / 60), opened = 0, named = 0, rewards = 0, conditions = 0, lines = 0;
                long clone = ed.NextFreeId(90000000);
                for (int i = 0; i < all.Count; i += step)
                {
                    QuestDraft d = ed.Open(all[i].Id);
                    opened++;
                    Assert.That(d.Places, Is.Not.Empty, "quest #" + i);
                    int level; int.TryParse(d.Level ?? "0", out level);
                    lines += d.SetLevel(level + 1);
                    long exp; long.TryParse(d.Exp ?? "0", out exp);
                    lines += d.SetExp(exp + 1);
                    lines += d.SetStartNpc(1);
                    if (d.Name.Length > 0) { named++; lines += d.SetText("name", d.Name + " x"); }
                    List<QuestCondition> c = d.Conditions();
                    if (c.Count > 0) { conditions++; lines += d.SetConditionCount(0, 7); }
                    foreach (string list in QuestDraft.RewardLists)
                    {
                        if (!d.HasList(list)) continue;
                        rewards++;
                        lines += d.AddReward(list, new QuestItem(1, 1, 3));
                        if (d.Rewards(list).Count > 1) lines += d.RemoveReward(list, 0);
                    }
                    if (i == 0)
                    {
                        QuestDraft n = ed.Clone(all[i].Id, clone);
                        Assert.That(n.Places.Count, Is.EqualTo(d.Places.Count), "the clone has the places of its source");
                        n.SetLevel(55);
                    }
                }
                Plan p = Planner.Build(data, new[] { Mod.Parse(mod.ToXml(), "round trip") }, new PlanOptions());
                TestContext.Progress.WriteLine("quests: " + all.Count + ", opened " + opened + ", named " + named + ", with conditions " + conditions
                    + ", reward lists " + rewards + ", lines " + lines + ", ops " + mod.Ops.Count + ", warnings " + p.Warnings.Count);
                foreach (PlanMessage w in p.Warnings.GetRange(0, Math.Min(5, p.Warnings.Count))) TestContext.Progress.WriteLine("  warning: " + w);
                Assert.That(p.Errors, Is.Empty, Fake.Dump(p));
                Assert.That(p.Conflicts, Is.Empty, Fake.Dump(p));
                Assert.That(named, Is.GreaterThan(0));
                Assert.That(lines, Is.GreaterThan(opened));
            }
        }
    }
}
