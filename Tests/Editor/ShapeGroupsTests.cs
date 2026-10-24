using System.Linq;
using NUnit.Framework;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class ShapeGroupsTests
    {
        [TestCase("===== Nail =====", "Nail")]
        [TestCase("**********外观Style**********", "外观Style")]
        [TestCase("  ===== Nail =====  ", "Nail")]
        [TestCase("--- Mouth ---", "Mouth")]
        [TestCase("~~~ Eyes ~~~", "Eyes")]
        [TestCase("=== Nail =====", "Nail")]
        [TestCase("\u3000===== 爪 =====\t", "爪")]
        [TestCase("=== Nail ---", null)]
        [TestCase("===", null)]
        [TestCase("---", null)]
        [TestCase("==Nail==", null)]
        [TestCase("bu---n", null)]
        [TestCase("== = ==", null)]
        [TestCase("====== ======", null)]
        public void SeparatorTitle(string name, string title) => Assert.That(ShapeGroups.SeparatorTitle(name), Is.EqualTo(title));

        [Test]
        public void Of_GroupsABodyAtItsSeparators()
        {
            var names = new[]
            {
                "Shape_A", "Shape_B", "===== Nail =====", "Nail_Long", "Toenail_Oval", "===== Group_C =====", "Spine_2", "belly",
                "===== Group_D =====", "Shape_D1", "===== Group_E =====", "Shape_E1", "Shape_E2",
            };
            var groups = ShapeGroups.Of(names);
            Assert.That(groups.Select(g => g.Title), Is.EqualTo(new[] { null, "Nail", "Group_C", "Group_D", "Group_E" }));
            Assert.That(groups.Select(g => g.Shapes.Count), Is.EqualTo(new[] { 2, 2, 2, 1, 2 }));
            Assert.That(groups.SelectMany(g => g.Shapes).Select(i => names[i]).Where(ShapeGroups.IsSeparator), Is.Empty);
        }

        [Test]
        public void Of_WithoutSeparatorsIsOneUntitledGroup_AndEmptyInputNone()
        {
            var groups = ShapeGroups.Of(new[] { "a", "b" });
            Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0].Title, Is.Null);
            Assert.That(groups[0].Shapes.Count, Is.EqualTo(2));
            Assert.That(ShapeGroups.Of(new string[0]), Is.Empty);
        }

        [Test]
        public void Of_KeysRepeatedTitlesByOccurrence()
        {
            var groups = ShapeGroups.Of(new[] { "=== A ===", "x", "=== A ===", "y" });
            Assert.That(groups.Select(g => g.Key), Is.EqualTo(new[] { "A#0", "A#1" }));
        }
    }
}
