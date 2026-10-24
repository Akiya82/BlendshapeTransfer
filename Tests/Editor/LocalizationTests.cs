using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEditor;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class LocalizationTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "bt-localization-test");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "en.json"), "{ \"a\": \"A {0}\", \"b\": \"B\", \"b.tip\": \"tip B\", \"bad\": \"{0}!\", \"n\": \"{0:0.0} mm\", \"c\": \"C {0}\", \"d\": \"D {0}\", \"e\": \"E\", \"brace\": \"{0} }\" }");
            File.WriteAllText(Path.Combine(folder, "ru.json"), "{ \"a\": \"Ру {0}\", \"bad\": \"{0\", \"c\": \"Ру\", \"d\": \"Д {0} {1}\", \"e\": \"\" }");
            File.WriteAllText(Path.Combine(folder, "xx.json"), "{ not json");
            Localization.FolderOverride = folder;
            Localization.Reload();
        }

        [TearDown]
        public void TearDown()
        {
            Localization.FolderOverride = null;
            Localization.Override = null;
            Localization.Missing = null;
            Localization.Reload();
            Directory.Delete(folder, true);
        }

        [Test]
        public void Get_FallsBackToEnglish_ThenToTheKey()
        {
            string missed = null;
            Localization.Missing = key => missed = key;
            Localization.Override = "ru";
            Assert.That(Localization.Get("a"), Is.EqualTo("Ру {0}"));
            Assert.That(Localization.Get("b"), Is.EqualTo("B"));
            Assert.That(Localization.Get("zzz"), Is.EqualTo("zzz"));
            Assert.That(missed, Is.EqualTo("zzz"));
        }

        [Test]
        public void Tip_FallsBackToEnglish_ElseNone()
        {
            Localization.Override = "ru";
            Assert.That(Localization.Tip("b"), Is.EqualTo("tip B"));
            Assert.That(Localization.Tip("a"), Is.Empty);
        }

        [Test]
        public void AMalformedFileReadsAsEmpty()
        {
            Localization.Override = "xx";
            Assert.That(Localization.Get("b"), Is.EqualTo("B"));
        }

        [Test]
        public void Format_AStringThatDoesNotFitItsArgumentsFallsBackToEnglish()
        {
            Localization.Override = "ru";
            Assert.That(Localization.Format("bad", 1), Is.EqualTo("1!"));
        }

        [Test]
        public void Format_ATranslationThatDropsOrAddsAPlaceholderFallsBackToEnglish()
        {
            Localization.Override = "ru";
            Assert.That(Localization.Format("c", 1), Is.EqualTo("C 1"), "a dropped argument");
            Assert.That(Localization.Format("d", 1), Is.EqualTo("D 1"), "an argument that is not there");
        }

        [Test]
        public void Format_UsesTheInvariantCulture()
        {
            var culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
                Assert.That(Localization.Format("n", 1.5f), Is.EqualTo("1.5 mm"));
            }
            finally { Thread.CurrentThread.CurrentCulture = culture; }
        }

        [Test]
        public void Format_AnEnglishTextThatThrowsGivesTheKey_AndReportsItMissing()
        {
            string missed = null;
            Localization.Missing = key => missed = key;
            Localization.Override = "ru";
            Assert.That(Localization.Format("brace", 1), Is.EqualTo("brace"));
            Assert.That(missed, Is.EqualTo("brace"));
        }

        [Test]
        public void Format_APlaceholderTextCalledWithoutArgumentsGivesTheKey()
        {
            string missed = null;
            Localization.Missing = key => missed = key;
            Localization.Override = "ru";
            Assert.That(Localization.Format("a"), Is.EqualTo("a"));
            Assert.That(missed, Is.EqualTo("a"));
            Assert.That(Localization.Format("b"), Is.EqualTo("B"), "a text without placeholders");
        }

        [Test]
        public void AnEmptyTranslationFallsBackToEnglish()
        {
            Localization.Override = "ru";
            Assert.That(Localization.Get("e"), Is.EqualTo("E"));
        }

        [Test]
        public void Current_WithAnOverride_ChangesWithoutTouchingThePreference()
        {
            const string pref = "Akiya82.BlendshapeTransfer.Language";
            var before = EditorPrefs.GetString(pref, "<unset>");
            Localization.Override = "en";
            Localization.Current = "ru";
            Assert.That(Localization.Current, Is.EqualTo("ru"));
            Assert.That(EditorPrefs.GetString(pref, "<unset>"), Is.EqualTo(before));
        }

        [Test]
        public void ReportText_RendersAFullReportFromTheEnglishFile()
        {
            Localization.FolderOverride = null;
            Localization.Reload();
            Localization.Override = "en";
            var missing = new List<string>();
            Localization.Missing = missing.Add;
            var report = new GarmentReport
            {
                Written = { "Bump", "Belly" },
                Skipped = { new SkippedShape { name = "Kept", reason = Msg.SkipKept }, new SkippedShape { name = "Far", reason = Msg.SkipNoReach } },
                BaseOffset = { new BaseWeight { name = "Belly", weight = 70f } },
                Fallback = 1,
                Singular = 2,
                WeightMismatch = 3,
                Clip = new ClipStats { Pushed = 4, MaxPush = 0.0025f, StillSkin = 5, StillLayer = 6, Intermediate = 7, Unresolved = 8 },
                MaxMove = 0.01f,
            };
            var warnings = new[] { new Message(Msg.EditsDropped), new Message(Msg.Singular, 2), new Message(Msg.UnmatchedBones, "Tail_1, Tail_2") };
            var info = new[] { new Message(Msg.FitWeight, "Belly", 0f, 70f) };
            var text = ReportText.Of("Top", report, warnings, info, 7, upToDate: false);
            var none = ReportText.Of("Top", null, new Message[0], new Message[0], 0, upToDate: true);
            foreach (var line in ReportText.Lines(report)) Assert.That(Localization.Tip(line.Key), Is.Not.Empty, line.Key);
            Assert.That(text, Does.Contain("(last transfer)"), text);
            Assert.That(text, Does.Contain("warnings: 7"), text);
            Assert.That(ReportText.Of("Top", report, warnings, info, 7, upToDate: true), Does.Not.Contain("(last transfer)"));
            Assert.That(none, Does.Not.Contain("report."), none);
            Assert.That(text, Does.Contain("2.5 mm"), text);
            Assert.That(missing, Is.Empty, text + none);
            Assert.That(text, Does.Not.Contain("report."), text);
        }

        [Test]
        public void EveryCoreMessageKeyHasEnglishText()
        {
            Localization.FolderOverride = null;
            Localization.Reload();
            var keys = typeof(Msg).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetValue(null)).ToList();
            Assert.That(keys, Is.Not.Empty);
            Assert.That(keys.Where(k => !Localization.Has("en", k)), Is.Empty);
        }

        [Test]
        public void EveryLanguageHasTheSameKeysAndPlaceholders()
        {
            Localization.FolderOverride = null;
            Localization.Reload();
            var en = Localization.Table("en");
            Assert.That(en, Is.Not.Empty);
            foreach (var (code, _) in Localization.Languages.Where(l => l.Code != "en"))
            {
                var other = Localization.Table(code);
                Assert.That(other.Keys.OrderBy(k => k), Is.EqualTo(en.Keys.OrderBy(k => k)), code);
                foreach (var kv in en) Assert.That(Placeholders(other[kv.Key]), Is.EqualTo(Placeholders(kv.Value)), $"{code}: {kv.Key}");
                foreach (var kv in other)
                {
                    Assert.That(kv.Value, Is.Not.Null.And.Not.Empty, $"{code}: {kv.Key}");
                    Assert.DoesNotThrow(() => string.Format(CultureInfo.InvariantCulture, kv.Value, Enumerable.Repeat<object>(1.5f, 10).ToArray()), $"{code}: {kv.Key}");
                }
            }
        }

        private static IEnumerable<string> Placeholders(string text) =>
            Regex.Matches(text, @"\{\d+(:[^}]*)?\}").Cast<Match>().Select(m => m.Value).OrderBy(s => s, System.StringComparer.Ordinal);
    }
}
