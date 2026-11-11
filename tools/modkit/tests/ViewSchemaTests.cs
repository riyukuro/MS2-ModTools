// The schema check of the Toolbench views: EditorSession.SchemaDir. The views print EditorSession.Report of the
// session's plan, so these tests read that text. Tables, attributes and the notes file are made up.
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace MS2.ModKit.Tests
{
    [TestFixture]
    public class ViewSchemaTests
    {
        const string C = "client:Xml.m2d", T = "table/pet.xml";
        const string Never = "attribute shine is never set on ms2/pet in the shipped table/pet.xml";
        const string Read = "attribute glow of ms2/pet is not in the shipped table/pet.xml; the client loader reads it (game/pets/petloader.cpp:5): int, how bright the pet is";

        static string Sets() { return "<edit archive=\"" + C + "\" entry=\"" + T + "\"><change at=\"pet[id=100]\"><set name=\"glow\" value=\"1\"/><set name=\"shine\" value=\"2\"/></change></edit>"; }

        // the cache "modkit schema build" writes, and a notes file
        static void Cache(Fake f, out string dir, out string notes)
        {
            dir = Path.Combine(f.Dir, "schema");
            var set = new SchemaSet();
            set.Build(f.Data, C, null);
            set.Save(dir);
            notes = Path.Combine(f.Dir, "notes.xml");
            File.WriteAllText(notes, "<notes><attr archive=\"" + C + "\" table=\"" + T + "\" element=\"ms2/pet\" name=\"glow\" source=\"client\" ref=\"game/pets/petloader.cpp:5\" use=\"int, how bright the pet is\"/></notes>");
        }

        static int Cli(out string stdout, params string[] argv)
        {
            System.Reflection.MethodInfo main = System.Reflection.Assembly.Load("modkit").EntryPoint;
            TextWriter o = System.Console.Out, e = System.Console.Error;
            var so = new StringWriter(); var se = new StringWriter();
            so.NewLine = "\n";
            System.Console.SetOut(so); System.Console.SetError(se);
            try { return (int)main.Invoke(null, new object[] { argv }); }
            finally { System.Console.SetOut(o); System.Console.SetError(e); stdout = so.ToString() + se.ToString(); }
        }

        [Test]
        public void WithoutTheSettingNothingIsChecked()
        {
            using (var f = new Fake())
            {
                string dir, notes;
                Cache(f, out dir, out notes);
                f.ModDir("m.one", Sets());
                foreach (string empty in new[] { null, "" })
                    using (var s = new EditorSession(f.ClientDir, null, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                    {
                            s.SchemaDir = empty;
                        s.SchemaNotes = notes;      // notes alone do nothing
                        Plan p = s.Plan();
                        string text = EditorSession.Report(p);
                        Assert.That(p.Warnings, Is.Empty, text);
                        Assert.That(text, Does.Not.Contain("never set"));
                        Assert.That(text, Does.Not.Contain("loader reads"));
                        Assert.That(text, Does.Not.Contain("schema"));
                    }
            }
        }

        [Test]
        public void WithTheSettingThePlanWarnsAndCitesTheNote()
        {
            using (var f = new Fake())
            {
                string dir, notes;
                Cache(f, out dir, out notes);
                f.ModDir("m.one", Sets());
                using (var s = new EditorSession(f.ClientDir, null, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                {
                    s.SchemaDir = dir;
                    s.SchemaNotes = notes;
                    Plan p = s.Plan();
                    string[] lines = EditorSession.Report(p).Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);
                    Assert.That(lines[0], Does.Contain("1 warning(s)"));
                    Assert.That(lines.Count(l => l.StartsWith("warning: ") && l.EndsWith(Never)), Is.EqualTo(1), string.Join("\n", lines));
                    Assert.That(lines.Count(l => l.StartsWith("note: ") && l.EndsWith(Read)), Is.EqualTo(1), string.Join("\n", lines));
                    Assert.That(p.Warnings.Single().ModId, Is.EqualTo("m.one"));

                    // without a notes file (null, or a file that is not there) the noted attribute is a warning too
                    foreach (string none in new[] { null, Path.Combine(f.Dir, "none.xml") })
                    {
                        s.SchemaNotes = none;
                        p = s.Plan();
                        Assert.That(p.Warnings.Count, Is.EqualTo(2), EditorSession.Report(p));
                        Assert.That(p.Warnings.Any(w => w.Text == "attribute glow is never set on ms2/pet in the shipped table/pet.xml"), EditorSession.Report(p));
                    }

                    // a folder without a cache: said once, nothing checked
                    s.SchemaDir = Path.Combine(f.Dir, "empty");
                    p = s.Plan();
                    Assert.That(p.Warnings, Is.Empty);
                    Assert.That(EditorSession.Report(p), Does.Contain("no schema is cached for this archive"));
                }
            }
        }

        // The sessions here hold the client only: a table both sides hold would add the warnings of the sync check.
        // The view's lines are the command line's: the same planner path, the same text.
        [Test]
        public void TheLinesAreTheOnesOfTheCommandLine()
        {
            using (var f = new Fake())
            {
                string dir, notes, mods = Path.Combine(f.Dir, "mods"), o;
                Cache(f, out dir, out notes);
                string mod = f.ModDir("m.one", Sets());
                Cli(out o, "validate", mod, "--client", f.ClientDir, "--schema", dir, "--notes", notes);
                string[] cli = o.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("never set") || l.Contains("loader reads")).ToArray();
                Assert.That(cli.Length, Is.EqualTo(2), "[" + o + "]");
                using (var s = new EditorSession(f.ClientDir, null, mods, Path.Combine(f.Dir, "ws")))
                {
                    s.SchemaDir = dir;
                    s.SchemaNotes = notes;
                    string[] view = EditorSession.Report(s.Plan()).Split(new[] { "\r\n" }, System.StringSplitOptions.None).Select(l => l.Trim()).Where(l => l.Contains("never set") || l.Contains("loader reads")).ToArray();
                    Assert.That(view, Is.EquivalentTo(cli));
                }
            }
        }

        // Apply checks too: its plan carries the warning, and a warning does not stop it.
        [Test]
        public void ApplyCarriesTheWarning()
        {
            using (var f = new Fake())
            {
                string dir, notes;
                Cache(f, out dir, out notes);
                f.ModDir("m.one", Sets());
                using (var s = new EditorSession(f.ClientDir, null, Path.Combine(f.Dir, "mods"), Path.Combine(f.Dir, "ws")))
                {
                    s.SchemaDir = dir;
                    s.SchemaNotes = notes;
                    ApplyResult r = s.Apply();
                    Assert.That(r.Applied, Is.True, EditorSession.Report(r));
                    Assert.That(r.Plan.Warnings.Any(w => w.Text == Never), EditorSession.Report(r));
                    Assert.That(r.Plan.Notes.Any(n => n.Text == Read), EditorSession.Report(r));
                }
            }
        }
    }
}
