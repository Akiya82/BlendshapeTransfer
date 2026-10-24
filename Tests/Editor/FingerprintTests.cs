using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class FingerprintTests
    {
        private Rig rig;
        private readonly List<Message> errors = new List<Message>();

        [SetUp] public void SetUp() => rig = new Rig("BT Fingerprint Test");
        [TearDown] public void TearDown() => rig.Dispose();

        private SkinnedMeshRenderer Body(params string[] extraBones)
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            return rig.AddRenderer("Body", mesh, "Hips", extraBones);
        }

        private SkinnedMeshRenderer Garment(string name, float z) =>
            rig.AddRenderer(name, MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), z), "Hips");

        private ulong[] Read(SkinnedMeshRenderer body, params SkinnedMeshRenderer[] garments)
        {
            var session = TransferSession.Open(body, garments, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            return session.Fingerprints();
        }

        [Test]
        public void StableAcrossReads()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            Assert.That(Read(body, top), Is.EqualTo(Read(body, top)));
        }

        [Test]
        public void ASettingChangesIt_AndChangingItBackRestoresIt()
        {
            var body = Body();
            var session = TransferSession.Open(body, new[] { Garment("Top", 0.002f) }, errors);
            var before = session.Fingerprints()[0];
            session.Garments[0].Data.settings.smoothIterations = 7;
            Assert.That(session.Fingerprints()[0], Is.Not.EqualTo(before));
            session.Garments[0].Data.settings.smoothIterations = TransferSettings.Tight().smoothIterations;
            Assert.That(session.Fingerprints()[0], Is.EqualTo(before));
        }

        [Test]
        public void TheInnerGarmentChangesTheOuterOne_UnlessNotWornWith_AndNeverTheOtherWay()
        {
            var session = TransferSession.Open(Body(), new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) }, errors);
            var before = session.Fingerprints();
            session.Garments[0].Data.settings.smoothIterations = 7;
            var after = session.Fingerprints();
            Assert.That(after[1], Is.Not.EqualTo(before[1]), "the skirt collides with the shorts");

            session.Garments[1].Data.notWornWith.Add(session.Garments[0].Data.rendererPath);
            var listed = session.Fingerprints()[1];
            session.Garments[0].Data.settings.smoothIterations = 9;
            Assert.That(session.Fingerprints()[1], Is.EqualTo(listed), "not worn with: the shorts do not matter");

            var shorts = session.Fingerprints()[0];
            session.Garments[1].Data.settings.smoothIterations = 9;
            Assert.That(session.Fingerprints()[0], Is.EqualTo(shorts), "an outer garment never changes an inner one");
        }

        [Test]
        public void NotWornWithOnTheInnerGarment_AlsoKeepsItOutOfTheOuterOne()
        {
            var session = TransferSession.Open(Body(), new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) }, errors);
            var before = session.Fingerprints()[1];
            session.Garments[0].Data.notWornWith.Add(session.Garments[1].Data.rendererPath);
            var listed = session.Fingerprints()[1];
            Assert.That(listed, Is.Not.EqualTo(before), "the skirt lost its collider");

            session.Garments[0].Data.settings.smoothIterations = 7;
            Assert.That(session.Fingerprints()[1], Is.EqualTo(listed), "the shorts list the skirt: they do not matter to it");
        }

        [Test]
        public void MovingTheAvatarDoesNotChangeIt_ScalingItDoes()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            var at = Read(body, top);
            rig.Root.transform.SetPositionAndRotation(new Vector3(1f, 0.5f, -2f), Quaternion.Euler(0f, 30f, 10f));
            Assert.That(Read(body, top), Is.EqualTo(at));
            rig.Root.transform.localScale = new Vector3(1.1f, 1.1f, 1.1f);
            Assert.That(Read(body, top), Is.Not.EqualTo(at));
        }

        [Test]
        public void AChangedUvOrAnUntickedVendorShapeOfTheSourceChangesIt()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            var mesh = top.sharedMesh;
            var uv = new Vector2[mesh.vertexCount];
            mesh.uv = uv;
            MeshFactory.AddShape(mesh, "Vendor Only", p => new Vector3(0f, 0f, 0.001f));
            var at = Read(body, top);

            // the same layout and shape names: only the content differs
            uv[0] = new Vector2(0.5f, 0.5f);
            mesh.uv = uv;
            var withUv = Read(body, top);
            Assert.That(withUv, Is.Not.EqualTo(at), "the output copies the UVs");
            mesh.ClearBlendShapes();
            MeshFactory.AddShape(mesh, "Vendor Only", p => new Vector3(0f, 0f, 0.002f));
            Assert.That(Read(body, top), Is.Not.EqualTo(withUv), "the output copies the vendor shapes");
        }

        [Test]
        public void ThePoseTheBodyWeightsTheTicksAndTheEditsChangeIt()
        {
            var body = Body();
            var top = rig.AddRenderer("Top", MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f), "Spine");
            var at = Read(body, top);

            rig.Bones["Spine"].localPosition += new Vector3(0.001f, 0f, 0f);
            var moved = Read(body, top);
            Assert.That(moved, Is.Not.EqualTo(at), "a bone of the garment moved");

            body.SetBlendShapeWeight(0, 30f);
            var session = TransferSession.Open(body, new[] { top }, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            var weighted = session.Fingerprints()[0];
            Assert.That(weighted, Is.Not.EqualTo(moved[0]), "a body shape weight");

            var bump = session.Garments[0].Data.Entry("Bump");
            bump.enabled = false;
            Assert.That(session.Fingerprints()[0], Is.Not.EqualTo(weighted), "a shape unticked");
            bump.enabled = true;

            var edit = ScriptableObject.CreateInstance<ShapeEdit>();
            try
            {
                edit.sculpt = new Vector3[top.sharedMesh.vertexCount];
                bump.frames.Add(edit);
                var edited = session.Fingerprints()[0];
                edit.sculpt[0] = new Vector3(0f, 0f, 0.001f);
                Assert.That(session.Fingerprints()[0], Is.Not.EqualTo(edited), "an edit changed in place");
            }
            finally { Object.DestroyImmediate(edit); }
        }

        [Test]
        public void RaisingThePipelineVersionChangesIt()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            var at = Read(body, top);
            var version = InputFingerprint.PipelineVersion;
            try
            {
                InputFingerprint.PipelineVersion = version + 1;
                Assert.That(Read(body, top), Is.Not.EqualTo(at));
            }
            finally { InputFingerprint.PipelineVersion = version; }
        }

        [Test]
        public void TheBoneMapChangesIt_AlsoWhenOnlyABoneNameBreaksATie()
        {
            rig.AddBone("Twin", rig.Bones["Hips"], Vector3.zero);
            var body = Body("Twin");
            var strap = rig.AddBone("Strap", rig.Bones["Hips"], Vector3.zero);
            var top = rig.AddRenderer("Top", MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f), "Strap");
            var before = TransferSession.Open(body, new[] { top }, errors);
            Assert.That(before, Is.Not.Null, string.Join("\n", errors));

            // positions, weights and meshes stay as they were
            strap.name = "Twin";
            var after = TransferSession.Open(body, new[] { top }, errors);
            Assert.That(after, Is.Not.Null, string.Join("\n", errors));

            Assert.That(after.Garments[0].Snapshot.BoneToBody, Is.Not.EqualTo(before.Garments[0].Snapshot.BoneToBody),
                "Hips and Twin are equally near the strap: its name picks the body bone");
            Assert.That(after.Fingerprints()[0], Is.Not.EqualTo(before.Fingerprints()[0]));
        }

        [Test]
        public void TheLowerGarmentListingTheUpperOne_ChangesOnlyTheUpperFingerprint()
        {
            var session = TransferSession.Open(Body(), new[] { Garment("Shorts", 0.002f), Garment("Skirt", 0.006f) }, errors);
            var before = session.Fingerprints();
            session.Garments[0].Data.notWornWith.Add(session.Garments[1].Data.rendererPath);
            var after = session.Fingerprints();
            Assert.That(after[0], Is.EqualTo(before[0]), "the lower garment does not depend on the list");
            Assert.That(after[1], Is.Not.EqualTo(before[1]), "the upper one lost its collider");
        }
    }
}
