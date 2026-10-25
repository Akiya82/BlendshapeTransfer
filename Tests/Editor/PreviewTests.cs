using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class PreviewTests
    {
        private const string Avatar = "BT Preview Test";
        private const string Temp = "Assets/BlendshapeTransferTests_tmp";
        private Rig rig;
        private bool rootExisted;
        private readonly List<Message> errors = new List<Message>();

        [SetUp]
        public void SetUp()
        {
            rig = new Rig(Avatar);
            rootExisted = AssetDatabase.IsValidFolder(OutputWriter.Root);
            if (!AssetDatabase.IsValidFolder(Temp)) AssetDatabase.CreateFolder("Assets", "BlendshapeTransferTests_tmp");
        }

        // NDMF's depth is the live editor's: the tests only ever add and take back their own raises, never assign it
        [TearDown]
        public void TearDown()
        {
            rig.Dispose();
            AssetDatabase.DeleteAsset(Temp);
            AssetDatabase.DeleteAsset(rootExisted ? $"{OutputWriter.Root}/{Avatar}" : OutputWriter.Root);
        }

        private static Mesh Saved(Mesh mesh, string name)
        {
            AssetDatabase.CreateAsset(mesh, $"{Temp}/{name}.asset");
            return mesh;
        }

        private SkinnedMeshRenderer Body()
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.1f, 0.1f, 41), MeshFactory.Range(-0.1f, 0.1f, 41), 0f);
            MeshFactory.AddShape(mesh, "Bump", MeshFactory.Bump(0.01f, 0.04f));
            return rig.AddRenderer("Body", Saved(mesh, "Body"), "Hips");
        }

        private SkinnedMeshRenderer Garment(string name, float z) =>
            rig.AddRenderer(name, Saved(MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), z), name), "Hips");

        private TransferSession Read(SkinnedMeshRenderer body, params SkinnedMeshRenderer[] garments)
        {
            var session = TransferSession.Open(body, garments, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            return session;
        }

        [Test]
        public void Preview_HidesTheRenderers_AndWritesEveryFlagBack_ThenLetsNdmfPreviewAgain()
        {
            var body = Body();
            var top = Garment("Top", 0.004f);
            var hair = new GameObject("Hair", typeof(MeshFilter), typeof(MeshRenderer)).GetComponent<MeshRenderer>();
            hair.transform.SetParent(rig.Root.transform, false);
            top.forceRenderingOff = true;
            var session = Read(body, top);
            var depth = NDMFPreview.DisablePreviewDepth;

            var preview = new SessionPreview(session);
            try
            {
                Assert.That(NDMFPreview.DisablePreviewDepth, Is.EqualTo(depth + 1));
                Assert.That(body.forceRenderingOff, Is.True);
                Assert.That(top.forceRenderingOff, Is.True);
                preview.SetOthers(new Renderer[] { hair });
                Assert.That(hair.forceRenderingOff, Is.True);
                preview.SetOthers(new Renderer[0]);
                Assert.That(hair.forceRenderingOff, Is.False, "shown again once no longer asked");
                preview.SetOthers(new Renderer[] { hair });
                // another tool holds NDMF's previews off too
                NDMFPreview.DisablePreviewDepth++;
                try
                {
                    preview.Dispose();
                    Assert.That(NDMFPreview.DisablePreviewDepth, Is.EqualTo(depth + 1), "only its own raise is taken back");
                }
                finally
                {
                    NDMFPreview.DisablePreviewDepth--;
                }
            }
            finally
            {
                // a second Dispose does nothing
                preview.Dispose();
            }

            Assert.That(NDMFPreview.DisablePreviewDepth, Is.EqualTo(depth));
            Assert.That(body.forceRenderingOff, Is.False);
            Assert.That(top.forceRenderingOff, Is.True, "hidden before the preview, hidden after it");
            Assert.That(hair.forceRenderingOff, Is.False);
        }

        [Test]
        public void TheNdmfDepth_IsNeverLoweredBelowZero()
        {
            // NDMF resets the depth to 0 on leaving play mode, possibly under a running preview
            Assert.That(SessionPreview.Lowered(0), Is.EqualTo(0));
            Assert.That(SessionPreview.Lowered(2), Is.EqualTo(1));
        }

        [Test]
        public void Pick_TakesTheNearestVisibleGarment_NeverTheBody()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            using (var preview = new SessionPreview(Read(body, inner, outer)))
            {
                var down = new Ray(new Vector3(0.01f, 0.01f, 1f), Vector3.back);
                Assert.That(preview.Pick(down, _ => true), Is.SameAs(outer));
                Assert.That(preview.Pick(down, r => r != outer), Is.SameAs(inner), "a garment with its eye off is skipped");
                Assert.That(preview.Pick(down, r => r == body), Is.Null, "the body is never picked");
                Assert.That(preview.Pick(new Ray(new Vector3(0.5f, 0f, 1f), Vector3.back), _ => true), Is.Null, "a miss");
            }
        }

        [Test]
        public void Draw_QueuesEveryMaterialSlotOfTheVisibleRenderers()
        {
            var body = Body();
            var top = Garment("Top", 0.004f);
            var material = new Material(Shader.Find("Hidden/Internal-Colored"));
            try
            {
                body.sharedMaterial = material;
                // one submesh, two slots: the second draws the last submesh again
                top.sharedMaterials = new[] { material, material };
                var camera = new GameObject("Camera").AddComponent<Camera>();
                using (var preview = new SessionPreview(Read(body, top)))
                {
                    Assert.That(preview.Draw(camera, _ => true), Is.EqualTo(3));
                    Assert.That(preview.Draw(camera, r => r != top), Is.EqualTo(1));
                }
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Show_TheDotsWaitForACompute_AndReassertHidesAgain()
        {
            var body = Body();
            var top = Garment("Top", 0.004f);
            var session = Read(body, top);
            // a garment that does not write the shape stays put while the skin rises 10 mm through it at 100
            session.Garments[0].Data.Entry("Bump").enabled = false;
            using (var preview = new SessionPreview(session))
            {
                preview.Show("Bump", 0f, true, true);
                Assert.That(preview.SkinCount, Is.EqualTo(0), "at weight 0 nothing clips");
                preview.Show("Bump", 100f, true, false);
                Assert.That(preview.SkinCount, Is.EqualTo(0), "the dots wait while a control is held");
                preview.Show("Bump", 100f, true, true);
                Assert.That(preview.SkinCount, Is.GreaterThan(0));
                preview.Show("Bump", 50f, true, false);
                Assert.That(preview.SkinCount, Is.EqualTo(0), "none rather than another weight's");
                preview.Show("Bump", 100f, false, true);
                Assert.That(preview.SkinCount, Is.EqualTo(0), "clipping off");

                body.forceRenderingOff = false;
                preview.Reassert();
                Assert.That(body.forceRenderingOff, Is.True);
                preview.SetOthers(new Renderer[] { top });
                preview.SetOthers(new Renderer[0]);
                Assert.That(top.forceRenderingOff, Is.True, "a session renderer stays hidden");
            }
        }
    }
}
