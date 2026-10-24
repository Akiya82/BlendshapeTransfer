using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class UpToDateTests
    {
        private const string Avatar = "BT UpToDate Test";
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

        private static string PathOf(GarmentState g) => AssetDatabase.GetAssetPath(g.Data);

        [Test]
        public void Transfer_WritesTheOutputStateWithTheFingerprintAndTheReport()
        {
            var session = Read(Body(), Garment("Top", 0.002f));
            Assert.That(session.Transfer(null), Is.True);

            var state = OutputWriter.StateAt(PathOf(session.Garments[0]));
            Assert.That(state, Is.Not.Null);
            Assert.That(state.fingerprint, Is.EqualTo(InputFingerprint.Hex(session.Fingerprints()[0])));
            Assert.That(state.report.Written, Is.EqualTo(new[] { "Bump" }));
            Assert.That(File.ReadAllText(PathOf(session.Garments[0])), Does.Contain("fingerprint: " + state.fingerprint), "saved to the file");
        }

        [Test]
        public void ARead_AfterAFalloffChange_TicksNothingNew_OnAGarmentWithAnOutput()
        {
            var body = Body();
            // 10 cm from the skin: beyond Tight's falloffZero, the bump does not reach it at first
            var top = Garment("Top", 0.1f);
            var session = Read(body, top);
            var data = session.Garments[0].Data;
            Assert.That(data.Entry("Bump"), Is.Null);
            data.settings.falloffZero = 0.2f;
            Assert.That(session.Transfer(null), Is.True);

            var again = Read(body, top);
            Assert.That(again.Garments[0].Reach["Bump"], Is.GreaterThan(TransferSession.ReachThreshold), "it reaches now");
            Assert.That(again.Garments[0].Data.Entry("Bump"), Is.Null, "a read adds no default tick after the first output");
            Assert.That(again.AllUpToDate(), Is.True);
        }

        /// <summary>A new Undo group that is never empty: PerformUndo on an empty one would undo whatever came before, the owner's work included.</summary>
        private static void Group()
        {
            Undo.IncrementCurrentGroup();
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
        }

        [Test]
        public void Undo_NeverRollsBackTheOutputState()
        {
            var session = Read(Body(), Garment("Top", 0.002f));
            Group();
            Assert.That(session.Transfer(null), Is.True);
            var data = session.Garments[0].Data;
            Group();
            Undo.RecordObject(data, "Transfer settings");
            data.settings.smoothIterations = 7;
            Group();
            Assert.That(session.Transfer(null), Is.True);
            var path = PathOf(session.Garments[0]);
            var written = OutputWriter.StateAt(path).fingerprint;

            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Undo.PerformUndo();

            Assert.That(data.settings.smoothIterations, Is.Not.EqualTo(7), "the settings went back");
            Assert.That(OutputWriter.StateAt(path).fingerprint, Is.EqualTo(written), "the state still says what the mesh on disk is");
            Assert.That(session.IsUpToDate(session.Garments[0], session.Fingerprints()[0]), Is.False, "the mesh on disk has smoothing 7");

            // back past the first transfer, which created the state
            Undo.PerformUndo();
            Assert.That(OutputWriter.StateAt(path), Is.Not.Null, "its creation is not undone either");
            Assert.That(OutputWriter.StateAt(path).fingerprint, Is.EqualTo(written));
        }

        [Test]
        public void ANewRead_TakesTheReportFromDisk()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            Assert.That(Read(body, top).Transfer(null), Is.True);

            var g = Read(body, top).Garments[0];

            Assert.That(g.Report.Written, Is.EqualTo(new[] { "Bump" }));
        }

        [Test]
        public void Write_LeavesTheStoredFingerprintClearedUntilTheStateIsWritten()
        {
            var session = Read(Body(), Garment("Top", 0.002f));
            Assert.That(session.Transfer(null), Is.True);
            var g = session.Garments[0];
            var path = PathOf(g);
            var written = OutputWriter.StateAt(path).fingerprint;

            OutputWriter.Write(OutputWriter.BuildMesh(g.Snapshot.Source, new List<OutputWriter.WrittenShape>()), path, g.Data);

            Assert.That(OutputWriter.StateAt(path).fingerprint, Is.Empty, "a crash before the state write leaves nothing to match");
            Assert.That(File.ReadAllText(path), Does.Not.Contain(written));
        }

        [Test]
        public void TheMeshSave_WritesTheOutputStateWithIt()
        {
            // Write clears the stored fingerprint before the mesh write and relies on this save alone to put that on disk
            var session = Read(Body(), Garment("Top", 0.002f));
            Assert.That(session.Transfer(null), Is.True);
            var path = PathOf(session.Garments[0]);
            var state = OutputWriter.StateAt(path);
            state.fingerprint = "cleared";
            EditorUtility.SetDirty(state);
            var mesh = Object.Instantiate(OutputWriter.OutputAt(path));
            mesh.name = Path.GetFileNameWithoutExtension(path);

            MeshAssetWriter.WriteOrOverwrite(mesh, path);

            Assert.That(File.ReadAllText(path), Does.Contain("fingerprint: cleared"));
        }

        [Test]
        public void NothingChanged_WritesNoAssetAndSkipsEveryGarment()
        {
            var session = Read(Body(), Garment("Inner", 0.002f), Garment("Outer", 0.006f));
            Assert.That(session.Transfer(false, null).Completed, Is.True);

            var run = session.Transfer(false, null);

            Assert.That(run.Completed && run.Written.Count == 0, Is.True);
            Assert.That(run.Skipped.Count, Is.EqualTo(2), "both reported as skipped");
        }

        [Test]
        public void AMiddleGarmentsChange_MakesItAndTheGarmentsAboveItStale_NotThoseBelow()
        {
            var session = Read(Body(), Garment("A", 0.002f), Garment("B", 0.004f), Garment("C", 0.006f));
            Assert.That(session.Transfer(false, null).Completed, Is.True);
            session.Garments[1].Data.settings.smoothIterations = 7;

            var f = session.Fingerprints();

            Assert.That(session.Garments.Select((g, k) => session.IsUpToDate(g, f[k])), Is.EqualTo(new[] { true, false, false }));
        }

        [Test]
        public void AfterANewRead_AStaleOuterGarmentGetsItsInnerOneComputedInMemoryOnly()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            Assert.That(Read(body, inner, outer).Transfer(null), Is.True);
            var session = Read(body, inner, outer);
            session.Garments[1].Data.settings.smoothIterations = 7;

            var run = session.Transfer(false, null);

            Assert.That(run.Written, Is.EqualTo(new[] { session.Garments[1] }), "the inner garment is not written");
            Assert.That(session.Garments[0].ResultsFingerprint, Is.EqualTo(session.Fingerprints()[0]), "but computed as a collider");
        }

        [Test]
        public void AllAgain_WritesEveryGarment()
        {
            var session = Read(Body(), Garment("Inner", 0.002f), Garment("Outer", 0.006f));
            Assert.That(session.Transfer(false, null).Completed, Is.True);
            Assert.That(session.Transfer(true, null).Written.Count, Is.EqualTo(2));
        }

        [Test]
        public void DataWithoutAnOutputStateIsStale()
        {
            var session = Read(Body(), Garment("Top", 0.002f));
            Assert.That(session.Transfer(null), Is.True);
            var state = OutputWriter.StateAt(PathOf(session.Garments[0]));
            AssetDatabase.RemoveObjectFromAsset(state);
            Object.DestroyImmediate(state, true);
            AssetDatabase.SaveAssets();
            Assert.That(session.IsUpToDate(session.Garments[0], session.Fingerprints()[0]), Is.False);
        }

        [Test]
        public void RaisingThePipelineVersionMakesEverythingStale()
        {
            var session = Read(Body(), Garment("Top", 0.002f));
            Assert.That(session.Transfer(null), Is.True);
            Assert.That(session.AllUpToDate(), Is.True);
            var version = InputFingerprint.PipelineVersion;
            try
            {
                InputFingerprint.PipelineVersion = version + 1;
                Assert.That(session.AllUpToDate(), Is.False);
            }
            finally { InputFingerprint.PipelineVersion = version; }
        }

        [Test]
        public void ANonFiniteRigidSizeTransfersAsTheDefaultItHashesAs()
        {
            var mesh = MeshFactory.Merge(
                MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f),
                MeshFactory.Box(new Vector3(0.0125f, 0.0025f, 0.0045f), 0.004f));
            var top = rig.AddRenderer("Top", Saved(mesh, "Top"), "Hips");
            var session = Read(Body(), top);
            session.Garments[0].Data.settings.rigidIslandSize = float.NaN;

            Assert.That(session.Transfer(null), Is.True);

            var output = OutputWriter.OutputAt(PathOf(session.Garments[0]));
            var frame = new Vector3[output.vertexCount];
            output.GetBlendShapeFrameVertices(output.GetBlendShapeIndex("Bump"), 0, frame, null, null);
            var box = Enumerable.Range(21 * 21, 8).ToArray();
            var p = mesh.vertices;
            foreach (var a in box)
            foreach (var b in box)
                Assert.That((p[a] + frame[a] - p[b] - frame[b]).magnitude, Is.EqualTo((p[a] - p[b]).magnitude).Within(1e-5f), "the box stays rigid, as with the default 30 mm");
        }

        [Test]
        public void ACancelledTransferLeavesTheWrittenGarmentsUpToDateAndTheOthersAsTheyWere()
        {
            var session = Read(Body(), Garment("Inner", 0.002f), Garment("Outer", 0.006f));

            var run = session.Transfer(false, (info, progress) => info.StartsWith("Outer"));

            Assert.That(run.Cancelled, Is.True);
            Assert.That(run.Written, Is.EqualTo(new[] { session.Garments[0] }));
            var f = session.Fingerprints();
            Assert.That(session.IsUpToDate(session.Garments[0], f[0]), Is.True);
            Assert.That(session.IsUpToDate(session.Garments[1], f[1]), Is.False);
            Assert.That(session.Garments[1].Report, Is.Null, "an interrupted garment keeps its previous report");
            Assert.That(session.Garments[1].Data.baseWeights, Is.Empty, "and gets its first fit records only with its write");
            Assert.That(session.Garments[0].Data.baseWeights, Is.Not.Empty, "as the written one did");
        }

        [Test]
        public void ACancelledGarmentKeepsItsPreviousReportStateAndResults()
        {
            var session = Read(Body(), Garment("Inner", 0.002f), Garment("Outer", 0.006f));
            Assert.That(session.Transfer(false, null).Completed, Is.True);
            var outer = session.Garments[1];
            var report = outer.Report;
            var stored = OutputWriter.StateAt(PathOf(outer)).fingerprint;
            var results = outer.ResultsFingerprint;
            var bump = outer.Results["Bump"];
            session.Garments[0].Data.settings.smoothIterations = 7;

            var run = session.Transfer(false, (info, _) => info.StartsWith("Outer"));

            Assert.That(run.Cancelled, Is.True);
            Assert.That(run.Written, Is.EqualTo(new[] { session.Garments[0] }), "the inner garment was stale too");
            Assert.That(outer.Report, Is.SameAs(report));
            Assert.That(OutputWriter.StateAt(PathOf(outer)).fingerprint, Is.EqualTo(stored));
            Assert.That(outer.ResultsFingerprint, Is.EqualTo(results).And.Not.EqualTo(0UL));
            Assert.That(outer.Results["Bump"], Is.SameAs(bump));
        }

        [Test]
        public void AFailureAsACollider_NamesTheGarment_AndChangesNoGarment()
        {
            var body = Body();
            var inner = Garment("Inner", 0.002f);
            var outer = Garment("Outer", 0.006f);
            Assert.That(Read(body, inner, outer).Transfer(null), Is.True);
            // a new read: the inner garment's results are not in memory, so the stale outer one has it computed as a collider
            var session = Read(body, inner, outer);
            session.Garments[1].Data.settings.smoothIterations = 7;
            var reports = session.Garments.Select(g => g.Report).ToList();
            var stored = OutputWriter.StateAt(PathOf(session.Garments[1])).fingerprint;
            TransferRun run;
            // the run logs what it caught; neither test runner may see it as an unexpected exception
            var logging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                run = session.Transfer(false, (info, _) => info.StartsWith("Inner") ? throw new System.Exception("Inner fails as a collider") : false);
            }
            finally { Debug.unityLogger.logEnabled = logging; }

            Assert.That(run.Failed, Is.SameAs(session.Garments[0]));
            Assert.That(run.FailedAsCollider, Is.True);
            Assert.That(run.Error, Is.EqualTo("Inner fails as a collider"));
            Assert.That(run.Written, Is.Empty);
            Assert.That(session.Garments[0].Report, Is.Not.Null.And.SameAs(reports[0]), "the reports read from disk");
            Assert.That(session.Garments[1].Report, Is.Not.Null.And.SameAs(reports[1]));
            Assert.That(session.Garments[0].ResultsFingerprint, Is.EqualTo(0UL), "no results in memory");
            Assert.That(OutputWriter.StateAt(PathOf(session.Garments[1])).fingerprint, Is.EqualTo(stored));
        }

        [Test]
        public void Save_AfterANewReadWithNothingStale_AssignsAndBindsAsAfterATransfer()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            Assert.That(Read(body, top).Transfer(null), Is.True);
            var session = Read(body, top);
            Assert.That(session.AllUpToDate(), Is.True);

            session.Save(true);

            Assert.That(top.sharedMesh, Is.EqualTo(OutputWriter.OutputAt(PathOf(session.Garments[0]))));
            var sync = top.GetComponent<ModularAvatarBlendshapeSync>();
            Assert.That(sync != null && sync.Bindings.Any(b => b.Blendshape == "Bump"), Is.True);
        }

        [Test]
        public void Save_SkipsAGarmentWhoseOutputHasNoState()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            var source = top.sharedMesh;
            var first = Read(body, top);
            Assert.That(first.Transfer(null), Is.True);
            var path = PathOf(first.Garments[0]);
            var state = OutputWriter.StateAt(path);
            AssetDatabase.RemoveObjectFromAsset(state);
            Object.DestroyImmediate(state, true);
            AssetDatabase.SaveAssets();
            var session = Read(body, top);

            Assert.DoesNotThrow(() => session.Save(true));

            Assert.That(top.sharedMesh, Is.SameAs(source), "data without an output state is not up to date and is not saved");
            Assert.That(top.GetComponent<ModularAvatarBlendshapeSync>(), Is.Null, "nor bound");
        }

        [Test]
        public void Save_SkipsAGarmentWhoseStoredFingerprintIsCleared()
        {
            var body = Body();
            var top = Garment("Top", 0.002f);
            var source = top.sharedMesh;
            var first = Read(body, top);
            Assert.That(first.Transfer(null), Is.True);
            var state = OutputWriter.StateAt(PathOf(first.Garments[0]));
            state.fingerprint = "";
            EditorUtility.SetDirty(state);
            AssetDatabase.SaveAssets();
            var session = Read(body, top);

            Assert.DoesNotThrow(() => session.Save(true));

            Assert.That(top.sharedMesh, Is.SameAs(source), "a mesh without the state it was written with is not saved");
            Assert.That(top.GetComponent<ModularAvatarBlendshapeSync>(), Is.Null, "nor bound");
        }
    }
}
