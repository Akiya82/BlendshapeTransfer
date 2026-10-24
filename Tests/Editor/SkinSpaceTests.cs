using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class SkinSpaceTests
    {
        [Test]
        public void WorldPoints_MatchUnitySkinning_OnRotatedScaledBoneAndOffsetRenderer()
        {
            using (var rig = new Rig())
            {
                var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 5), MeshFactory.Range(-0.05f, 0.05f, 5), 0f);
                var renderer = rig.AddRenderer("Garment", mesh, (v, p) => new[] { ("Chest", 0.7f), ("Spine", 0.3f) });
                renderer.transform.localPosition = new Vector3(0.2f, 0.1f, 0f);
                renderer.transform.localScale = Vector3.one * 2f;
                rig.Bones["Chest"].localRotation = Quaternion.Euler(30f, 45f, 0f);
                rig.Bones["Chest"].localScale = Vector3.one * 1.5f;

                var world = SkinSpace.Of(renderer, mesh).WorldPoints(mesh.vertices);
                var baked = new Mesh();
                renderer.BakeMesh(baked, true);
                var local = baked.vertices;
                for (var v = 0; v < local.Length; v++)
                {
                    var expected = renderer.transform.localToWorldMatrix.MultiplyPoint3x4(local[v]);
                    Assert.That((expected - world[v]).magnitude, Is.LessThan(1e-5f), $"vertex {v}");
                }
                Object.DestroyImmediate(baked);
            }
        }

        [Test]
        public void MeshVector_InvertsWorldVector()
        {
            using (var rig = new Rig())
            {
                var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 2), MeshFactory.Range(-0.05f, 0.05f, 2), 0f);
                var renderer = rig.AddRenderer("Garment", mesh, "Chest");
                rig.Bones["Chest"].localRotation = Quaternion.Euler(10f, 80f, 20f);
                rig.Bones["Chest"].localScale = new Vector3(2f, 0.5f, 1f);
                var skin = SkinSpace.Of(renderer, mesh);
                var d = new Vector3(0.01f, -0.02f, 0.03f);

                Assert.That((skin.MeshVector(0, skin.WorldVector(0, d)) - d).magnitude, Is.LessThan(1e-7f));
                Assert.That(skin.SingularCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void MeshVector_IsZeroForSingularSkinning_ButNotForAScaledArmature()
        {
            using (var rig = new Rig())
            {
                var mesh = MeshFactory.Grid(MeshFactory.Range(-5f, 5f, 2), MeshFactory.Range(-5f, 5f, 2), 0f);
                var renderer = rig.AddRenderer("Garment", mesh, "Chest");
                rig.Bones["Chest"].localScale = Vector3.one * 0.01f;
                Assert.That(SkinSpace.Of(renderer, mesh).SingularCount, Is.EqualTo(0), "a uniformly small armature is not singular");

                rig.Bones["Chest"].localScale = new Vector3(1f, 1f, 0f);
                var skin = SkinSpace.Of(renderer, mesh);
                Assert.That(skin.SingularCount, Is.EqualTo(4));
                Assert.That(skin.MeshVector(0, Vector3.one), Is.EqualTo(Vector3.zero));
            }
        }

        [Test]
        public void Differs_SeesPureRotation()
        {
            var a = Matrix4x4.TRS(Vector3.one, Quaternion.identity, Vector3.one);
            var b = Matrix4x4.TRS(Vector3.one, Quaternion.Euler(0f, 1f, 0f), Vector3.one);
            Assert.That(SkinSpace.Differs(a, b, 1e-4f), Is.True);
            Assert.That(SkinSpace.Differs(a, a, 1e-4f), Is.False);
        }
    }
}
