using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class ScaffoldTests
    {
        [Test]
        public void Rig_BuildsAnAvatarOutsideTheOwnersScene()
        {
            var owner = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            using (var rig = new Rig())
            {
                var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 3), MeshFactory.Range(-0.1f, 0.1f, 3), 0f);
                var renderer = rig.AddRenderer("Body", mesh, "Hips");
                Assert.That(AvatarPaths.RootOf(renderer.transform), Is.EqualTo(rig.Root.transform));
                Assert.That(renderer.gameObject.scene, Is.EqualTo(rig.Scene));
                Assert.That(renderer.sharedMesh.bindposes.Length, Is.EqualTo(1));
            }
            Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), Is.EqualTo(owner));
        }

        [Test]
        public void BuiltInPresets_AreDistinctCopies()
        {
            var a = TransferSettings.Tight();
            var b = TransferSettings.Tight();
            a.clearance = 1f;
            Assert.That(b.clearance, Is.EqualTo(0.001f));
            Assert.That(TransferSettings.Rigid().rigidIslandSize, Is.GreaterThan(100f));
            Assert.That(TransferSettings.BuiltIns.Length, Is.EqualTo(4));
        }

        [Test]
        public void TransferData_GetOrAddEntry_AddsOnce()
        {
            var data = ScriptableObject.CreateInstance<TransferData>();
            data.GetOrAddEntry("belly").enabled = false;
            data.GetOrAddEntry("belly");
            Assert.That(data.shapes.Count, Is.EqualTo(1));
            Assert.That(data.Entry("belly").enabled, Is.False);
            Object.DestroyImmediate(data);
        }
    }
}
