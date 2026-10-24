using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class BoneMapTests
    {
        [Test]
        public void Map_MatchesByPositionThenNameThenAncestor()
        {
            using (var rig = new Rig())
            {
                var twist = rig.AddBone("SpineTwist", rig.Bones["Spine"], Vector3.zero);
                var garmentRoot = new GameObject("GarmentHips").transform;
                garmentRoot.SetParent(rig.Root.transform, false);
                garmentRoot.position = rig.Bones["Hips"].position;
                var nearSpine = new GameObject("Spine_copy").transform;
                nearSpine.SetParent(garmentRoot, false);
                nearSpine.position = rig.Bones["Spine"].position + new Vector3(0.0005f, 0f, 0f);
                var twistCopy = new GameObject("SpineTwist").transform;
                twistCopy.SetParent(garmentRoot, false);
                twistCopy.position = twist.position;
                var skirt = new GameObject("Skirt_1").transform;
                skirt.SetParent(garmentRoot, false);
                skirt.position = garmentRoot.position + new Vector3(0f, -0.2f, 0.15f);
                var orphan = new GameObject("Orphan").transform;
                orphan.position = new Vector3(5f, 5f, 5f);

                var body = new[] { rig.Bones["Hips"], rig.Bones["Spine"], twist, rig.Bones["Chest"] };
                var garment = new[] { rig.Bones["Chest"], nearSpine, twistCopy, skirt, orphan };
                var unmatched = new List<string>();

                var map = BoneMap.Map(garment, body, unmatched);

                Assert.That(map, Is.EqualTo(new[] { 3, 1, 2, 0, -1 }));
                Assert.That(unmatched, Is.EqualTo(new[] { "Orphan" }));
                Object.DestroyImmediate(orphan.gameObject);
            }
        }

        [Test]
        public void Related_UsesTheNearestAncestorAmongTheRenderersBones()
        {
            using (var rig = new Rig())
            {
                var mesh = MeshFactory.Grid(MeshFactory.Range(0f, 0.1f, 2), MeshFactory.Range(0f, 0.1f, 2), 0f);
                var renderer = rig.AddRenderer("Body", mesh, (v, p) => new[] { (v < 2 ? "Chest" : "UpperArm.L", 1f) }, "Shoulder.L");
                var bones = new BoneData(mesh, renderer.bones);
                int chest = 0, arm = 1, shoulder = 2;

                Assert.That(bones.Related(arm, shoulder), Is.True);
                Assert.That(bones.Related(shoulder, chest), Is.True);
                Assert.That(bones.Related(arm, chest), Is.False);
                Assert.That(bones.Significant[0], Is.EqualTo(new[] { chest }));
                Assert.That(bones.RelatedToAny(0, new int[0]), Is.True, "no garment bones: no filter");
            }
        }
    }
}
