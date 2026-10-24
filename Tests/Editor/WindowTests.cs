using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer.Tests
{
    public sealed class WindowTests
    {
        private const string Avatar = "BT Window Test";
        private const string Temp = "Assets/BlendshapeTransferTests_tmp";
        /// <summary>The sections the window marks as drawn.</summary>
        private static readonly string[] Sections = { "garments", "settings", "shapes", "result", "bottom" };
        private Rig rig;
        private bool rootExisted;
        private ShapeTransferWindow window;

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
            if (window != null) window.Close();
            Localization.Override = null;
            Localization.Missing = null;
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

        private SkinnedMeshRenderer Top(string name = "Top") =>
            rig.AddRenderer(name, Saved(MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), 0.002f), name), "Hips");

        /// <summary>
        /// Layout and Repaint right now (Repaint() from a script may never reach a window without focus), failing on anything the
        /// window logs as an error or exception: IMGUI logs those instead of throwing them into the test.
        /// </summary>
        private static void Paint(EditorWindow w)
        {
            var repaint = typeof(EditorWindow).GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(repaint, Is.Not.Null, "EditorWindow.RepaintImmediately is gone: the window cannot be painted from a test");
            var logged = new List<string>();
            void Log(string message, string stack, LogType type)
            {
                if (type == LogType.Exception || type == LogType.Error) logged.Add(message);
            }
            Application.logMessageReceived += Log;
            try
            {
                repaint.Invoke(w, null);
            }
            finally
            {
                Application.logMessageReceived -= Log;
            }
            Assert.That(logged, Is.Empty, "the window logged while drawing");
        }

        private void OpenTransferred()
        {
            var body = Body();
            var garment = Top();
            window = ScriptableObject.CreateInstance<ShapeTransferWindow>();
            window.AllOpen = true;
            window.ShowUtility();
            window.position = new Rect(100f, 100f, 620f, 900f);
            window.SetUp(body, new[] { garment });
            window.Read();
            window.RunTransfer(false);
        }

        [Test]
        public void SettingsOfDataNotOnDisk_SurviveAReadAndADrop()
        {
            var body = Body();
            var top = Top();
            window = Unpainted();
            window.SetUp(body, new[] { top });
            window.Read();
            Assert.That(window.Session, Is.Not.Null);
            window.Session.Garments[0].Data.settings.smoothIterations = 17;

            window.Read();
            Assert.That(window.Session.Garments[0].Data.settings.smoothIterations, Is.EqualTo(17), "read again");

            window.SetUp(body, new[] { top, Top("Second") });
            window.Read();
            Assert.That(window.Session.Garments.Single(g => g.Renderer == top).Data.settings.smoothIterations, Is.EqualTo(17), "after a drop");
        }

        [Test]
        public void EverySectionDraws()
        {
            Localization.Override = "en";
            var missing = new List<string>();
            Localization.Missing = missing.Add;
            OpenTransferred();

            Paint(window);

            Assert.That(window.Drawn, Is.SupersetOf(Sections), "drawn: " + string.Join(", ", window.Drawn));
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void EverySectionDrawsInEveryLanguage_WithNoKeyMissingInEnglish()
        {
            var missing = new List<string>();
            Localization.Missing = missing.Add;
            Localization.Override = "en";
            OpenTransferred();
            foreach (var (code, _) in Localization.Languages)
            {
                Localization.Override = code;
                Paint(window);
                Assert.That(window.Drawn, Is.SupersetOf(Sections), $"{code}: drawn {string.Join(", ", window.Drawn)}");
            }
            Assert.That(missing.Distinct(), Is.Empty, "keys looked up without English text");
        }

        /// <summary>The status line of a window that reads the meshes after a Transfer, and a Save when asked, done without it.</summary>
        private string StatusOfANewRead(bool save)
        {
            Localization.Override = "en";
            var body = Body();
            var top = Top();
            var errors = new List<Message>();
            var session = TransferSession.Open(body, new[] { top }, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            Assert.That(session.Transfer(null), Is.True);
            if (save) session.Save(true);
            window = Unpainted();
            window.SetUp(body, new[] { top });
            window.Read();
            return window.Status();
        }

        /// <summary>A window the test never paints; shown all the same, since Close() throws on a window that never was.</summary>
        private static ShapeTransferWindow Unpainted()
        {
            var w = ScriptableObject.CreateInstance<ShapeTransferWindow>();
            w.ShowUtility();
            return w;
        }

        [Test]
        public void ADropForgetsThisWindowsTransfer_ReadingAgainKeepsIt()
        {
            Localization.Override = "en";
            var body = Body();
            var top = Top();
            window = Unpainted();
            window.SetUp(body, new[] { top });
            window.RunTransfer(false);
            Assert.That(window.Status(), Does.StartWith("Transferred at "));

            window.Read();
            Assert.That(window.Status(), Does.StartWith("Transferred at "), "read again in the same window");

            window.SetUp(body, new[] { top });
            window.Read();
            Assert.That(window.Status(), Is.EqualTo("Up to date"), "after a drop the status comes from disk");
        }

        [Test]
        public void ANewReadAfterSave_SaysSaved() => Assert.That(StatusOfANewRead(true), Is.EqualTo("Saved"));

        [Test]
        public void ANewReadWithoutSave_SaysUpToDate() => Assert.That(StatusOfANewRead(false), Is.EqualTo("Up to date"));

        [Test]
        public void APoseOrWeightChangeSinceTheRead_TurnsTransferOff_UntilReadAgain()
        {
            Localization.Override = "en";
            var body = Body();
            var top = Top();
            window = Unpainted();
            window.position = new Rect(100f, 100f, 620f, 900f);
            window.SetUp(body, new[] { top });
            window.Read();
            Paint(window);
            Assert.That(window.CanTransfer, Is.True, "just read");

            // looking at a shape on the body must not fit the garments to it
            body.SetBlendShapeWeight(0, 100f);
            Paint(window);
            Assert.That(window.CanTransfer, Is.False, "a body weight changed");

            body.SetBlendShapeWeight(0, 0f);
            rig.Bones["Hips"].localPosition += new Vector3(0f, 0.01f, 0f);
            Paint(window);
            Assert.That(window.CanTransfer, Is.False, "a bone moved");

            window.Read();
            Paint(window);
            Assert.That(window.CanTransfer, Is.True, "read again in the new pose");
        }

        private SkinnedMeshRenderer Garment(string name, float z, string ownShape = null)
        {
            var mesh = MeshFactory.Grid(MeshFactory.Range(-0.05f, 0.05f, 21), MeshFactory.Range(-0.05f, 0.05f, 21), z);
            if (ownShape != null) MeshFactory.AddShape(mesh, ownShape, MeshFactory.Bump(0.01f, 0.04f));
            return rig.AddRenderer(name, Saved(mesh, name), "Hips");
        }

        [Test]
        public void SaveAndSyncMovingShapesWithFitRecords_LeaveTransferOn_AndOnlyAChangedGarmentIsTransferred()
        {
            Localization.Override = "en";
            var body = Body();
            body.SetBlendShapeWeight(0, 50f);
            // its own Bump is replaced by the transferred one, which Save sets to the body's 50
            var inner = Garment("Inner", 0.002f, "Bump");
            var outer = Garment("Outer", 0.004f);
            window = Unpainted();
            window.position = new Rect(100f, 100f, 620f, 900f);
            window.SetUp(body, new[] { inner, outer });
            window.Read();
            Assert.That(window.Session.Garments.Select(g => g.Renderer), Is.EqualTo(new[] { inner, outer }));
            window.Session.Garments[0].Data.Entry("Bump").overwrite = true;
            window.RunTransfer(false);
            Assert.That(window.LastRun.Written.Count, Is.EqualTo(2));

            window.Save();
            Assert.That(AssetDatabase.GetAssetPath(inner.sharedMesh), Does.StartWith(OutputWriter.Root), "the inner garment shows its output");
            // MA Blendshape Sync drives the bound shapes with the body's weights in edit mode
            inner.SetBlendShapeWeight(inner.sharedMesh.GetBlendShapeIndex("Bump"), 50f);
            Paint(window);
            Assert.That(window.CanTransfer, Is.True, "Save and Sync moved only a garment shape with a fit record");

            body.SetBlendShapeWeight(0, 80f);
            Paint(window);
            Assert.That(window.CanTransfer, Is.True, "the snapshot shows the body's recorded Bump, not its live weight");
            body.SetBlendShapeWeight(0, 50f);

            window.Session.Garments[1].Data.settings.smoothIterations = 7;
            window.RunTransfer(false);
            Assert.That(window.LastRun.Written.Select(g => g.Renderer), Is.EqualTo(new[] { outer }), "the inner garment stayed up to date");
        }

        [Test]
        public void TheStatus_ReportsASaveRatherThanTheTransferBefore_AndComesFromTheRenderersAfterUndoOrRedo()
        {
            Localization.Override = "en";
            var body = Body();
            var top = Top();
            window = Unpainted();
            window.position = new Rect(100f, 100f, 620f, 900f);
            window.SetUp(body, new[] { top });
            window.RunTransfer(false);
            window.RunTransfer(false);
            Assert.That(window.Status(), Is.EqualTo("Nothing needed a transfer"));

            Undo.IncrementCurrentGroup();
            Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
            window.Save();
            Assert.That(window.Status(), Does.StartWith("Saved at "), "the save, not the transfer before it");

            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(AssetDatabase.GetAssetPath(top.sharedMesh), Does.StartWith(Temp), "the save was undone");
            // Unity calls Undo.undoRedoPerformed from the editor loop after PerformUndo, which a test does not run
            window.UndoRedoPerformed();
            Paint(window);
            Assert.That(window.Status(), Is.EqualTo("Up to date"), "an undone save is not reported as saved");

            Undo.PerformRedo();
            Assert.That(AssetDatabase.GetAssetPath(top.sharedMesh), Does.StartWith(OutputWriter.Root), "the save was redone");
            window.UndoRedoPerformed();
            Paint(window);
            Assert.That(window.Status(), Is.EqualTo("Saved"), "a redone save shows the outputs, not \"Transferred at …, not saved\"");
        }

        private static void Move(Mesh mesh)
        {
            var v = mesh.vertices;
            v[0] += new Vector3(0f, 0f, 0.001f);
            mesh.vertices = v;
        }

        [Test]
        public void ReadMeshChanged_OnlyWhenAReadMeshsFileComesBackWithOtherContent()
        {
            var body = Body();
            var top = Top();
            var errors = new List<Message>();
            var session = TransferSession.Open(body, new[] { top }, errors);
            Assert.That(session, Is.Not.Null, string.Join("\n", errors));
            var source = session.Garments[0].Snapshot.Source;
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var bodyPath = AssetDatabase.GetAssetPath(body.sharedMesh);
            Assert.That(session.Transfer(null), Is.True);
            session.Save(true);
            var output = AssetDatabase.GetAssetPath(session.Garments[0].Data);
            Assert.That(AssetDatabase.GetAssetPath(top.sharedMesh), Is.EqualTo(output), "the garment shows its output now");

            // the fixture's meshes were dirty (bind poses set after CreateAsset): Transfer's and Save's SaveAssets imported them unchanged
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { sourcePath, bodyPath }), Is.False, "imported unchanged");
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { output }), Is.False, "the output Transfer and Save wrote");
            Move(source);
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { "Assets/Other.fbx" }), Is.False, "an unrelated file");
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { "Assets/Other.fbx", sourcePath }), Is.True, "the garment's source, changed");
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { bodyPath }), Is.False, "the body, unchanged");
            Move(body.sharedMesh);
            Assert.That(ShapeTransferWindow.ReadMeshChanged(session, new[] { bodyPath }), Is.True, "the body, changed");
            Assert.That(ShapeTransferWindow.ReadMeshChanged(null, new[] { sourcePath }), Is.False, "nothing read");
        }

        [Test]
        public void SetOverwrite_OnAShapeWithoutAnEntry_LeavesItUnticked()
        {
            var data = ScriptableObject.CreateInstance<TransferData>();
            try
            {
                ShapeTransferWindow.SetOverwrite(data, "Belly", true);
                Assert.That(data.Entry("Belly").enabled, Is.False);
                Assert.That(data.Entry("Belly").overwrite, Is.True);
            }
            finally
            {
                Undo.ClearUndo(data);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void SetTicked_IsOneUndoStep()
        {
            var data = ScriptableObject.CreateInstance<TransferData>();
            try
            {
                data.GetOrAddEntry("A").enabled = false;
                Undo.IncrementCurrentGroup();
                // never an empty group: PerformUndo would undo whatever came before it
                Undo.RegisterCreatedObjectUndo(new GameObject("Undo sentinel"), "Undo sentinel");
                ShapeTransferWindow.SetTicked(data, new[] { "A", "B", "C" }, true);
                Assert.That(data.shapes.Count(e => e.enabled), Is.EqualTo(3));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(data.shapes.Select(e => e.name), Is.EqualTo(new[] { "A" }));
                Assert.That(data.Entry("A").enabled, Is.False);
            }
            finally
            {
                Undo.ClearUndo(data);
                Object.DestroyImmediate(data);
            }
        }
    }
}
