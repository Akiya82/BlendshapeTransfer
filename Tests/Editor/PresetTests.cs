using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class PresetTests
    {
        [Test]
        public void ABuiltInPresetsSettingsShowItsName()
        {
            foreach (var (name, create) in TransferSettings.BuiltIns) Assert.That(Presets.Matching(create(), Presets.BuiltIns())?.Name, Is.EqualTo(name));
        }

        [Test]
        public void OneChangedFieldIsCustom()
        {
            var s = TransferSettings.Tight();
            s.smoothIterations = 3;
            Assert.That(Presets.Matching(s, Presets.BuiltIns()), Is.Null);
        }

        [Test]
        public void TypingTheRadiusBackInMillimetresShowsThePresetAgain()
        {
            var s = TransferSettings.Tight();
            s.blendRadius = TransferSettings.Metres(35f);
            Assert.That(Presets.Matching(s, Presets.BuiltIns()), Is.Null);
            s.blendRadius = TransferSettings.Metres(40f);
            Assert.That(Presets.Matching(s, Presets.BuiltIns())?.Name, Is.EqualTo("Tight"));
        }

        [Test]
        public void EveryBuiltInLengthRoundTripsThroughMillimetres()
        {
            foreach (var (_, create) in TransferSettings.BuiltIns)
            {
                var s = create();
                foreach (var f in new[] { s.blendRadius, s.fieldRadius, s.hybridNear, s.hybridFar, s.falloffFull, s.falloffZero, s.rigidIslandSize, s.clearance })
                    Assert.That(TransferSettings.Metres(UnityEngine.Mathf.Round(f * 1000f)), Is.EqualTo(f));
            }
        }

        [Test]
        public void NegativeZeroAndNonFiniteValuesAreCanonical()
        {
            var a = TransferSettings.Tight();
            var b = TransferSettings.Tight();
            a.falloffFull = 0f;
            b.falloffFull = -0f;
            b.blendRadius = float.NaN;
            b.hybridFar = float.NaN;
            b.clearance = float.PositiveInfinity;
            Assert.That(Presets.Same(a, b), Is.True);
        }

        [Test]
        public void ASavedPresetEqualToABuiltInShowsTheBuiltIn()
        {
            var all = Presets.BuiltIns();
            all.Add(new Presets.Entry { Name = "My Tight", Settings = TransferSettings.Tight() });
            Assert.That(Presets.Matching(TransferSettings.Tight(), all)?.Name, Is.EqualTo("Tight"));
        }

        [Test]
        public void EverySettingsFieldChangesTheWords()
        {
            foreach (var f in typeof(TransferSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var s = TransferSettings.Tight();
                var v = f.GetValue(s);
                f.SetValue(s, v switch { float x => x + 0.001f, int i => i + 1, bool b => !b, SamplerMode m => m == SamplerMode.Field ? SamplerMode.Blend : SamplerMode.Field, _ => v });
                Assert.That(Presets.Same(s, TransferSettings.Tight()), Is.False, f.Name);
            }
        }

        [Test]
        public void WordsCoverEverySettingsField()
        {
            var fields = typeof(TransferSettings).GetFields(BindingFlags.Public | BindingFlags.Instance).Length;
            Assert.That(Presets.Words(new TransferSettings()).Count(), Is.EqualTo(fields), "a new settings field must enter the words");
        }
    }
}
