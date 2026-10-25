using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed partial class ShapeTransferWindow
    {
        [SerializeField] private string previewShape;
        [SerializeField] private float previewWeight = 100f;
        [SerializeField] private bool showClipping;
        [SerializeField] private bool hideOthers;
        private SessionPreview preview;
        /// <summary>Per renderer of the preview, whether it is drawn; from the scene when the preview starts.</summary>
        private readonly Dictionary<Renderer, bool> eyes = new Dictionary<Renderer, bool>();
        /// <summary>When a left mouse button last went down or up in a Scene View.</summary>
        private double lastSceneMouse = double.MinValue;
        /// <summary>The Scene View shows the main stage, not prefab mode; tests replace it.</summary>
        internal static Func<bool> InMainStage = () => StageUtility.GetCurrentStage() is MainStage;
        /// <summary>
        /// The current garment changed outside the window's GUI: its next OnGUI drops the keyboard focus, so a text field being edited
        /// does not carry its text into the next garment's settings.
        /// </summary>
        private bool clearFocus;

        internal SkinnedMeshRenderer CurrentGarment => current;

        internal void NoteSceneMouse(double now) => lastSceneMouse = now;

        /// <summary>
        /// The selection Unity makes from a Scene View click must not override the preview's pick: a selection change within half a
        /// second of the last mouse-down or mouse-up there is left alone. Timed from the mouse-down too, so a mouse-up the view never
        /// sees does not block the hierarchy for good.
        /// </summary>
        internal bool IgnoresSelection(double now) => now - lastSceneMouse < 0.5;

        internal SessionPreview Preview => preview;

        internal bool HideOthers
        {
            get => hideOthers;
            set
            {
                hideOthers = value;
                preview?.SetOthers(value ? OtherRenderers() : Enumerable.Empty<Renderer>());
                SceneView.RepaintAll();
            }
        }

        /// <summary>
        /// Starts the preview while the Edit tab shows read meshes, and ends it otherwise or when the session was replaced. It runs in the
        /// window's own GUI, never inside a Scene View's, where NDMF's pending reset would overwrite the flags just set.
        /// </summary>
        private void SyncPreview()
        {
            var wanted = tab == EditTab && session != null;
            if (preview != null && (!wanted || preview.Session != session)) EndPreview();
            if (!wanted || preview != null) return;
            try
            {
                preview = new SessionPreview(session);
            }
            catch (Exception e)
            {
                // once, not on every Layout: the status line says why and the window goes back to the Transfer tab
                Debug.LogException(e);
                statusOverride = L.Format("status.previewFailed", e.Message);
                tab = TransferTab;
                return;
            }
            eyes.Clear();
            eyes[session.Body] = InScene(session.Body);
            foreach (var g in session.Garments) eyes[g.Renderer] = InScene(g.Renderer);
            preview.SetOthers(hideOthers ? OtherRenderers() : Enumerable.Empty<Renderer>());
            SceneView.beforeSceneGui += BeforeSceneGui;
            SceneView.duringSceneGui += DuringSceneGui;
            Selection.selectionChanged += SelectionChanged;
            SceneView.RepaintAll();

            static bool InScene(Renderer r) => r.enabled && r.gameObject.activeInHierarchy;
        }

        private void EndPreview()
        {
            if (preview == null) return;
            SceneView.beforeSceneGui -= BeforeSceneGui;
            SceneView.duringSceneGui -= DuringSceneGui;
            Selection.selectionChanged -= SelectionChanged;
            preview.Dispose();
            preview = null;
            SceneView.RepaintAll();
        }

        /// <summary>The avatar's renderers outside the session, which Hide other meshes hides.</summary>
        private IEnumerable<Renderer> OtherRenderers() =>
            session.Avatar.GetComponentsInChildren<Renderer>(true).Where(r => r != session.Body && session.Garments.All(g => g.Renderer != r)).ToList();

        private bool Visible(Renderer r) => eyes.TryGetValue(r, out var shown) && shown;

        /// <summary>Before the Scene View camera renders: the preview meshes for this camera.</summary>
        private void BeforeSceneGui(SceneView view)
        {
            if (Event.current.type == EventType.Repaint) DrawPreview(view.camera);
        }

        /// <summary>
        /// One Scene View repaint of the preview: the checks the window's Layout would make (the window may be hidden behind a tab), the
        /// shown shape brought up to date unless a control is held or a text field is being edited, then the draw; the draw calls made.
        /// </summary>
        internal int DrawPreview(Camera camera)
        {
            if (preview == null || !InMainStage()) return 0;
            // ponytail: each Scene View repaint rechecks the pose, the ticked shapes and the fingerprints (twice with clipping); cache them per change if a large avatar lags
            var progressShown = false;
            try
            {
                if (SceneCheck()) return 0;
                preview.Reassert();
                var names = PreviewShapes();
                var first = names.FirstOrDefault();
                if (!names.Contains(previewShape) && previewShape != first)
                {
                    previewShape = first;
                    Repaint();
                }
                if (previewShape == null) preview.ShowRest();
                else
                {
                    // the meshes follow a dragged weight slider; the compute waits until no control is held and no text field is edited
                    var idle = GUIUtility.hotControl == 0 && !EditorGUIUtility.editingTextField;
                    var counts = (preview.SkinCount, preview.LayerCount);
                    preview.Show(previewShape, previewWeight, showClipping, idle, (info, progress) =>
                    {
                        progressShown = true;
                        EditorUtility.DisplayProgressBar(L.Get("window.title"), L.Format("progress.preview", info), progress);
                    });
                    // the window shows the counts, and they change here, in the Scene View
                    if (counts != (preview.SkinCount, preview.LayerCount)) Repaint();
                }
                return preview.Draw(camera, Visible);
            }
            catch (Exception e)
            {
                // once, not on every repaint: the preview ends and the status line says why
                Debug.LogException(e);
                statusOverride = L.Format("status.previewFailed", e.Message);
                tab = TransferTab;
                EndPreview();
                Repaint();
                return 0;
            }
            finally
            {
                // another tool's progress bar is not ours to clear
                if (progressShown) EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// The window's Layout checks, made from the Scene View: a destroyed body, garment, read mesh or data drops the read meshes and
        /// with them the preview; the pose check feeds the label. True when there is no session left.
        /// </summary>
        internal bool SceneCheck()
        {
            if (session == null) return true;
            if (SessionGone())
            {
                Drop("garments.gone");
                Repaint();
                return true;
            }
            var was = poseChanged;
            poseChanged = session.PoseChanged();
            // the window may show the warning and an enabled Transfer button
            if (poseChanged != was) Repaint();
            return false;
        }

        private void DuringSceneGui(SceneView view)
        {
            if (preview == null || !InMainStage()) return;
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                NoteSceneMouse(EditorApplication.timeSinceStartup);
                // not on the label, and not while the View tool pans; not used: Unity's own selection still happens
                if (!Tools.viewToolActive && !LabelRect(view).Contains(e.mousePosition)) ScenePick(HandleUtility.GUIPointToWorldRay(e.mousePosition));
            }
            // rawType: Unity's picking may have used the mouse-up already
            else if (e.rawType == EventType.MouseUp && e.button == 0) NoteSceneMouse(EditorApplication.timeSinceStartup);
            if (e.type == EventType.Repaint) DrawDots();
            DrawSceneLabel(view);
        }

        /// <summary>A Scene View click: the nearest visible garment under it becomes current; true when one did.</summary>
        internal bool ScenePick(Ray ray)
        {
            if (preview == null || !InMainStage()) return false;
            var hit = preview.Pick(ray, Visible);
            if (hit == null) return false;
            current = hit;
            clearFocus = true;
            Repaint();
            return true;
        }

        /// <summary>A session garment selected in the hierarchy becomes current, except the selection Unity makes from a Scene View click.</summary>
        private void SelectionChanged()
        {
            if (IgnoresSelection(EditorApplication.timeSinceStartup)) return;
            var picked = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<SkinnedMeshRenderer>() : null;
            if (picked == null || session == null || session.Garments.All(g => g.Renderer != picked)) return;
            current = picked;
            clearFocus = true;
            Repaint();
        }

        private void DrawDots()
        {
            if (!showClipping) return;
            foreach (var g in session.Garments)
            {
                if (!Visible(g.Renderer)) continue;
                if (preview.Skin.TryGetValue(g.Renderer, out var skin)) Dots(skin, Color.red);
                if (preview.Layer.TryGetValue(g.Renderer, out var layer)) Dots(layer, new Color(1f, 0.55f, 0f));
            }

            static void Dots(List<Vector3> points, Color color)
            {
                using (new Handles.DrawingScope(color))
                    foreach (var p in points) Handles.DotHandleCap(0, p, Quaternion.identity, HandleUtility.GetHandleSize(p) * 0.025f, EventType.Repaint);
            }
        }

        private static Rect LabelRect(SceneView view)
        {
            var height = view.camera.pixelRect.height / EditorGUIUtility.pixelsPerPoint;
            return new Rect(8f, height - 78f, 360f, 70f);
        }

        private void DrawSceneLabel(SceneView view)
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(LabelRect(view), EditorStyles.helpBox);
            GUILayout.Label(L.Format("edit.label", previewShape ?? "—", previewWeight), EditorStyles.boldLabel);
            // always drawn: the pose check runs in the Repaint pass, and Layout and Repaint must draw the same controls
            GUILayout.Label(poseChanged ? L.Get("edit.poseChangedShort") : "", EditorStyles.miniLabel);
            if (GUILayout.Button(L.Content("edit.endPreview"), GUILayout.Width(140f)))
            {
                tab = TransferTab;
                EndPreview();
                Repaint();
            }
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private void DrawEditTab()
        {
            Mark("edit");
            if (session == null)
            {
                foreach (var e in errors) EditorGUILayout.HelpBox(L.Text(e), MessageType.Error);
                DrawReadPrompt();
                return;
            }
            if (poseChanged)
            {
                // the preview keeps showing the read meshes
                EditorGUILayout.HelpBox(L.Get("garments.poseChanged"), MessageType.Warning);
                if (GUILayout.Button(L.Content("garments.readAgain")))
                {
                    Read();
                    GUIUtility.ExitGUI();
                }
            }
            DrawPreviewControls();
            DrawLayers();
            if (Current() != null) DrawSettingsSection();
        }

        private void DrawPreviewControls()
        {
            var names = PreviewShapes();
            if (names.Count == 0) EditorGUILayout.HelpBox(L.Get("edit.noShapes"), MessageType.Info);
            EditorGUI.BeginChangeCheck();
            var index = 0;
            var weight = previewWeight;
            var clipping = showClipping;
            if (names.Count > 0)
            {
                var labels = PreviewLabels(names).Select(l => new GUIContent(l)).ToArray();
                index = EditorGUILayout.Popup(L.Content("edit.shape"), Math.Max(0, names.IndexOf(previewShape)), labels);
                weight = EditorGUILayout.Slider(L.Content("edit.weight"), previewWeight, 0f, 100f);
                clipping = EditorGUILayout.ToggleLeft(L.Content("edit.showClipping"), showClipping);
                if (showClipping && preview != null) EditorGUILayout.LabelField(L.Format("edit.clippingCounts", preview.SkinCount, preview.LayerCount), EditorStyles.miniLabel);
            }
            var others = EditorGUILayout.ToggleLeft(L.Content("edit.hideOthers"), hideOthers);
            if (!EditorGUI.EndChangeCheck()) return;
            if (names.Count > 0)
            {
                previewShape = names[index];
                previewWeight = weight;
            }
            if (others != hideOthers) HideOthers = others;
            SceneView.RepaintAll();
            if (clipping == showClipping) return;
            // the counts line comes or goes
            showClipping = clipping;
            GUIUtility.ExitGUI();
        }

        /// <summary>The body shapes ticked on at least one garment, in body order, separators left out.</summary>
        private List<string> PreviewShapes()
        {
            var mesh = session.Surface.Mesh;
            var result = new List<string>();
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                var name = mesh.GetBlendShapeName(s);
                if (!ShapeGroups.IsSeparator(name) && session.Garments.Any(g => g.Data.Entry(name) is ShapeEntry e && e.enabled)) result.Add(name);
            }
            return result;
        }

        /// <summary>The names as the popup shows them: under their separator's title, as section 3 groups them.</summary>
        private string[] PreviewLabels(List<string> names)
        {
            var mesh = session.Surface.Mesh;
            var all = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
            var titles = new Dictionary<string, string>();
            foreach (var group in ShapeGroups.Of(all))
                foreach (var s in group.Shapes)
                    titles[all[s]] = group.Title;
            return names.Select(n => titles.TryGetValue(n, out var t) && t != null ? $"{Plain(t)}/{Plain(n)}" : Plain(n)).ToArray();

            // a slash would open a submenu of its own
            static string Plain(string s) => s.Replace('/', '\u2215');
        }

        private void DrawLayers()
        {
            EditorGUILayout.LabelField(L.Content("edit.layers"), EditorStyles.boldLabel);
            LayerRow(session.Body, false);
            foreach (var g in session.Garments) LayerRow(g.Renderer, true);
        }

        private void LayerRow(Renderer renderer, bool garment)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var shown = Visible(renderer);
                var eye = new GUIContent(EditorGUIUtility.IconContent(shown ? "animationvisibilitytoggleon" : "animationvisibilitytoggleoff").image, L.Get("edit.eye"));
                if (GUILayout.Button(eye, EditorStyles.label, GUILayout.Width(22f)))
                {
                    eyes[renderer] = !shown;
                    SceneView.RepaintAll();
                }
                var style = garment && renderer == current ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(renderer.name, style) && garment && renderer != current)
                {
                    current = (SkinnedMeshRenderer)renderer;
                    // a text field being edited must not carry its text into the next garment's settings
                    GUIUtility.keyboardControl = 0;
                    GUIUtility.ExitGUI();
                }
            }
        }
    }
}
