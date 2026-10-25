using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>
    /// Tools/Blendshape Transfer: a header with the language, one page of numbered folding sections (avatar and garments, settings,
    /// shapes, result) and a fixed bottom bar. IMGUI draws every event twice (layout, then input or repaint) and both passes must draw
    /// the same controls, so a change that adds or removes controls ends the event with GUIUtility.ExitGUI, and folds are drawn from
    /// their stored state (a new state shows from the next event).
    /// </summary>
    internal sealed partial class ShapeTransferWindow : EditorWindow
    {
        private const string Pref = "Akiya82.BlendshapeTransfer.";
        [SerializeField] private SkinnedMeshRenderer body;
        [SerializeField] private List<SkinnedMeshRenderer> garments = new List<SkinnedMeshRenderer>();
        /// <summary>The garment sections 2 and 3 show: the selected row, kept by renderer through reordering.</summary>
        [SerializeField] private SkinnedMeshRenderer current;
        [SerializeField] private bool syncWithBody = true;
        /// <summary>The meshes were read; with no session (a script reload drops it) section 1 offers to read again.</summary>
        [SerializeField] private bool readWanted;
        [SerializeField] private string search = "";
        [SerializeField] private bool reachOnly = true;
        internal const int TransferTab = 0, EditTab = 1;
        [SerializeField] private int tab;
        private TransferSession session;
        /// <summary>Per garment, from the last Layout event: whether its output is up to date.</summary>
        private bool[] fresh = new bool[0];
        /// <summary>From the last Layout event: a bone moved or a shape weight changed since the read.</summary>
        private bool poseChanged;
        /// <summary>
        /// From the last Layout event, with nothing stale and no Transfer or Save in this window: at least one garment's stored report
        /// wrote shapes, and every such garment shows its output.
        /// </summary>
        private bool showsOutputs;
        /// <summary>Key of why the read meshes were dropped when the owner did not drop them.</summary>
        private string droppedBy;
        private readonly List<Message> errors = new List<Message>();
        private ReorderableList list;
        private bool listChanged;
        private Vector2 scroll;
        private List<Presets.Entry> presets;
        /// <summary>Data of garments that are not on disk yet, kept across drops so their settings survive the next read.</summary>
        private readonly Dictionary<SkinnedMeshRenderer, TransferData> carried = new Dictionary<SkinnedMeshRenderer, TransferData>();
        [SerializeField] private List<SkinnedMeshRenderer> carriedRenderers = new List<SkinnedMeshRenderer>();
        [SerializeField] private List<TransferData> carriedData = new List<TransferData>();
        private List<ShapeGroup> groups;
        private TransferRun lastRun;
        private DateTime? transferredAt;
        private DateTime? savedAt;
        /// <summary>A failure the last action reported (save or restore failed); cleared by the next read, Transfer, Save, drop, Undo or Redo.</summary>
        private string statusOverride;
        /// <summary>Tests: every fold open, and the sections drawn in the last Repaint.</summary>
        internal bool AllOpen;
        internal readonly HashSet<string> Drawn = new HashSet<string>();

        [MenuItem("Tools/Akiya82/Blendshape Transfer")]
        public static void Open() => GetWindow<ShapeTransferWindow>();

        private void OnEnable()
        {
            carried.Clear();
            for (var i = 0; i < Math.Min(carriedRenderers.Count, carriedData.Count); i++)
                if (carriedRenderers[i] != null && carriedData[i] != null) carried[carriedRenderers[i]] = carriedData[i];
            titleContent = new GUIContent(L.Get("window.title"));
            list = new ReorderableList(garments, typeof(SkinnedMeshRenderer), true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, L.Content("garments.list")),
                drawElementCallback = DrawGarmentRow,
                onReorderCallback = _ =>
                {
                    // the read meshes stay; the order enters the fingerprints
                    if (session != null) session.Reorder(garments);
                    listChanged = true;
                },
                onAddCallback = _ =>
                {
                    garments.Add(null);
                    Drop(null);
                    listChanged = true;
                },
                onRemoveCallback = l =>
                {
                    garments.RemoveAt(l.index);
                    Drop(null);
                    listChanged = true;
                },
                onSelectCallback = l =>
                {
                    current = l.index >= 0 && l.index < garments.Count ? garments[l.index] : null;
                    listChanged = true;
                },
            };
            EditorApplication.playModeStateChanged += PlayModeChanged;
            EditorApplication.projectChanged += ProjectChanged;
            ImportWatch.Imported += Imported;
            Undo.undoRedoPerformed += UndoRedoPerformed;
        }

        private void OnDisable()
        {
            // before a script reload too: the record of the hidden renderers lives in memory
            EndPreview();
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorApplication.projectChanged -= ProjectChanged;
            ImportWatch.Imported -= Imported;
            Undo.undoRedoPerformed -= UndoRedoPerformed;
        }

        /// <summary>
        /// An undone or redone reorder brings back the layer indices; the list follows them. Any Undo or Redo shows at once, and the status
        /// no longer claims a save or a transfer the step may have undone or redone: it comes from the renderers, as after a new read.
        /// </summary>
        internal void UndoRedoPerformed()
        {
            statusOverride = null;
            savedAt = transferredAt = null;
            if (session != null && session.FollowLayerIndices())
            {
                garments.Clear();
                garments.AddRange(session.Garments.Select(g => g.Renderer));
            }
            Repaint();
            SceneView.RepaintAll();
        }

        private void PlayModeChanged(PlayModeStateChange change)
        {
            Drop(null);
            Repaint();
        }

        private void ProjectChanged()
        {
            presets = null;
            L.Reload();
            Repaint();
        }

        private void Imported(string[] paths)
        {
            if (!ReadMeshChanged(session, paths)) return;
            Drop("garments.reimported");
            Repaint();
        }

        /// <summary>
        /// Whether a mesh the session read (the body's, or a garment's source) is gone, or is in one of the imported files and no
        /// longer has the content it was read with. Content, not the path alone: every SaveAssets (Transfer, Save, Ctrl+S) saves a
        /// source that is dirty in memory and imports it again unchanged.
        /// </summary>
        internal static bool ReadMeshChanged(TransferSession session, IEnumerable<string> imported)
        {
            if (session == null) return false;
            var files = new HashSet<string>(imported);
            return Changed(session.Surface.Mesh, session.BodyContent) || session.Garments.Any(g => Changed(g.Snapshot.Source, g.Content));

            bool Changed(Mesh mesh, ulong content) =>
                mesh == null || files.Contains(AssetDatabase.GetAssetPath(mesh)) && InputFingerprint.MeshContent(mesh) != content;
        }

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout) Drawn.Clear();
            DrawHeader();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox(L.Get("window.exitPlayMode"), MessageType.Info);
                return;
            }
            // a released slider, a left text field or a changed setting lets the preview recompute: the Scene Views redraw after the
            // window's input (asked before drawing, since a setting that adds controls ends the event with ExitGUI)
            if (preview != null && Event.current.type != EventType.Layout && Event.current.type != EventType.Repaint) SceneView.RepaintAll();
            if (session != null && SessionGone()) Drop("garments.gone");
            if (Event.current.type == EventType.Layout)
            {
                Refresh();
                SyncPreview();
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (tab == EditTab) DrawEditTab();
            else
            {
                DrawGarmentsSection();
                if (session != null && Current() != null)
                {
                    DrawSettingsSection();
                    DrawShapesSection();
                }
                DrawResultSection();
            }
            EditorGUILayout.EndScrollView();
            DrawBottomBar();
        }

        /// <summary>The body, a garment, a read mesh or a garment's data was destroyed since the read.</summary>
        private bool SessionGone() =>
            session.Body == null || session.Surface.Mesh == null || session.Garments.Any(g => g.Renderer == null || g.Snapshot.Source == null || g.Data == null);

        internal int Tab
        {
            get => tab;
            set => tab = value;
        }

        partial void DrawSettingsSection();
        partial void DrawShapesSection();
        partial void DrawResultSection();

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var tabs = new[] { L.Content("tab.transfer"), L.Content("tab.edit") };
                var width = tabs.Sum(t => EditorStyles.toolbarButton.CalcSize(t).x) + 8f;
                var picked = GUILayout.Toolbar(tab, tabs, EditorStyles.toolbarButton, GUILayout.Width(width));
                if (picked != tab)
                {
                    tab = picked;
                    GUIUtility.ExitGUI();
                }
                GUILayout.FlexibleSpace();
                var label = L.Content("header.language");
                GUILayout.Label(label, EditorStyles.miniLabel, Width(label, EditorStyles.miniLabel));
                var languages = L.Languages;
                var index = Math.Max(0, Array.FindIndex(languages, l => l.Code == L.Current));
                var pick = EditorGUILayout.Popup(index, languages.Select(l => l.Name).ToArray(), EditorStyles.toolbarPopup, GUILayout.Width(80f));
                if (pick == index) return;
                L.Current = languages[pick].Code;
                titleContent = new GUIContent(L.Get("window.title"));
                GUIUtility.ExitGUI();
            }
        }

        private void DrawBottomBar()
        {
            Mark("bottom");
            var stale = Stale();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanTransfer))
                {
                    if (GUILayout.Button(WhyOff(L.Content("bottom.transfer")), GUILayout.Height(28f)))
                    {
                        RunTransfer(false);
                        GUIUtility.ExitGUI();
                    }
                    var all = WhyOff(L.Content("bottom.allAgain"));
                    if (GUILayout.Button(all, GUILayout.Height(28f), Width(all, GUI.skin.button)))
                    {
                        RunTransfer(true);
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUI.DisabledScope(stale != 0))
                {
                    if (GUILayout.Button(L.Content("bottom.save"), GUILayout.Height(28f)))
                    {
                        Save();
                        GUIUtility.ExitGUI();
                    }
                }
                var sync = L.Content("bottom.sync");
                syncWithBody = EditorGUILayout.ToggleLeft(sync, syncWithBody, Width(sync, EditorStyles.label, 24f));
            }
            EditorGUILayout.LabelField(Status(), EditorStyles.miniLabel);
        }

        /// <summary>
        /// Transfer and All again are on, from the last Layout event: never against a snapshot whose pose or shape weights changed since
        /// the read, which would fit the garments to the changed body. Save stays on: it works from disk.
        /// </summary>
        internal bool CanTransfer => body != null && garments.Count > 0 && !poseChanged;

        /// <summary>While the pose changed since the read, the button's tooltip says why it is off.</summary>
        private GUIContent WhyOff(GUIContent content)
        {
            if (poseChanged) content.tooltip = L.Get("garments.poseChanged");
            return content;
        }

        /// <summary>Garments that need a transfer, from the last Layout event; -1 when nothing is read.</summary>
        private int Stale() => session == null ? -1 : fresh.Count(f => !f);

        internal string Status()
        {
            var stale = Stale();
            if (statusOverride != null) return statusOverride;
            if (lastRun?.Failed != null)
                return L.Format(lastRun.FailedAsCollider ? "status.failedCollider" : "status.failed", lastRun.Failed.Renderer.name, lastRun.Error);
            // M counts the garments the run had to write: those written plus those still stale
            if (lastRun != null && lastRun.Cancelled) return L.Format("status.cancelled", lastRun.Written.Count, lastRun.Written.Count + Math.Max(stale, 0));
            if (session == null) return L.Get("status.notRead");
            if (stale > 0) return L.Format("status.needTransfer", stale);
            if (lastRun != null && lastRun.Completed && lastRun.Written.Count == 0) return L.Get("status.nothingToTransfer");
            if (savedAt != null) return L.Format("status.savedAt", savedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
            if (transferredAt != null) return L.Format("status.transferredAt", transferredAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
            // Sync is not checked: a renderer shows its output only after Save, which binds in the same step
            return L.Get(showsOutputs ? "status.saved" : "status.upToDate");
        }

        private void Refresh()
        {
            showsOutputs = false;
            poseChanged = false;
            if (session == null)
            {
                fresh = new bool[0];
                return;
            }
            var fingerprints = session.Fingerprints();
            fresh = session.Garments.Select((g, k) => session.IsUpToDate(g, fingerprints[k])).ToArray();
            poseChanged = session.PoseChanged();
            if (savedAt != null || transferredAt != null || fresh.Contains(false)) return;
            // a garment that wrote nothing keeps its own mesh
            var wrote = session.Garments.Where(g => g.Report != null && g.Report.Written.Count > 0).ToList();
            showsOutputs = wrote.Count > 0 && wrote.All(g => g.Renderer.sharedMesh == OutputWriter.OutputAt(AssetDatabase.GetAssetPath(g.Data)));
        }

        /// <summary>Unity serializes lists, not dictionaries: the lists bring the carried data through a script reload.</summary>
        private void MirrorCarried()
        {
            carriedRenderers = carried.Keys.ToList();
            carriedData = carried.Values.ToList();
        }

        internal TransferSession Session => session;

        /// <summary>What the last Transfer in this window did; null after a read, a drop or a Save.</summary>
        internal TransferRun LastRun => lastRun;

        private GarmentState Current() => session?.Garments.FirstOrDefault(g => g.Renderer == current);

        /// <summary>Tests: the body and garments a script would otherwise pick in section 1.</summary>
        internal void SetUp(SkinnedMeshRenderer newBody, IEnumerable<SkinnedMeshRenderer> newGarments)
        {
            body = newBody;
            garments.Clear();
            garments.AddRange(newGarments);
            current = garments.FirstOrDefault();
            Drop(null);
        }

        internal void Read()
        {
            // the session is replaced: the preview ends and the next Layout starts it on the new one
            EndPreview();
            droppedBy = null;
            lastRun = null;
            statusOverride = null;
            try
            {
                EditorUtility.DisplayProgressBar(L.Get("window.title"), L.Get("progress.reading"), 0f);
                session = TransferSession.Open(body, garments, errors, carried);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                errors.Clear();
                errors.Add(new Message(Msg.Exception, e.Message));
                session = null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            readWanted = session != null;
            if (session == null) return;
            // a read in between must not lose the settings of data that is not on disk yet
            foreach (var g in session.Garments)
                if (!AssetDatabase.Contains(g.Data)) carried[g.Renderer] = g.Data;
            MirrorCarried();
            garments.Clear();
            garments.AddRange(session.Garments.Select(g => g.Renderer));
            if (!garments.Contains(current)) current = garments.FirstOrDefault();
            Refresh();
        }

        /// <summary>
        /// Forgets the read meshes, the errors of the last read and what this window did with them, so the next read's status comes from
        /// disk; why is a message key when the owner did not ask for it.
        /// </summary>
        private void Drop(string why)
        {
            EndPreview();
            foreach (var dead in carried.Keys.Where(r => r == null).ToList()) carried.Remove(dead);
            MirrorCarried();
            session = null;
            readWanted = false;
            droppedBy = why;
            lastRun = null;
            transferredAt = savedAt = null;
            statusOverride = null;
            errors.Clear();
            fresh = new bool[0];
            showsOutputs = poseChanged = false;
        }

        internal void RunTransfer(bool all)
        {
            if (session == null) Read();
            if (session == null) return;
            statusOverride = null;
            try
            {
                lastRun = session.Transfer(all, (info, progress) => EditorUtility.DisplayCancelableProgressBar(L.Get("window.title"), info, progress));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            if (lastRun.Written.Count > 0)
            {
                transferredAt = DateTime.Now;
                savedAt = null;
            }
            Refresh();
        }

        internal void Save()
        {
            try
            {
                session.Save(syncWithBody);
                savedAt = DateTime.Now;
                // the status reports the save, not the transfer before it
                lastRun = null;
                statusOverride = null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                statusOverride = L.Format("status.saveFailed", e.Message);
            }
        }

        /// <summary>A section header that folds; when open, the section counts as drawn for the tests.</summary>
        private bool Section(string id, GUIContent title)
        {
            var open = Fold("Section." + id, title, EditorStyles.foldoutHeader);
            if (open) Mark(id);
            return open;
        }

        /// <summary>A fold remembered in the editor preferences, drawn from its stored state.</summary>
        private bool Fold(string id, GUIContent title, GUIStyle style = null, bool openByDefault = true)
        {
            style ??= EditorStyles.foldout;
            if (AllOpen)
            {
                EditorGUILayout.Foldout(true, title, true, style);
                return true;
            }
            var key = Pref + "Fold." + id;
            var open = EditorPrefs.GetBool(key, openByDefault);
            var now = EditorGUILayout.Foldout(open, title, true, style);
            if (now != open)
            {
                EditorPrefs.SetBool(key, now);
                Repaint();
            }
            return open;
        }

        private void Mark(string id)
        {
            if (Event.current.type == EventType.Repaint) Drawn.Add(id);
        }

        private static GUIContent Title(string key, params object[] args) => new GUIContent(L.Format(key, args), L.Tip(key));

        private static GUILayoutOption Width(GUIContent content, GUIStyle style, float extra = 8f) => GUILayout.Width(style.CalcSize(content).x + extra);

        /// <summary>A built-in editor icon with a tooltip from the language files.</summary>
        private static GUIContent Icon(string name, string key) => new GUIContent(EditorGUIUtility.IconContent(name).image, L.Get(key));
    }

    /// <summary>Tells open windows which assets were imported, so a window can drop a snapshot whose mesh changed under it.</summary>
    internal sealed class ImportWatch : AssetPostprocessor
    {
        internal static event Action<string[]> Imported;

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Length > 0) Imported?.Invoke(imported);
        }
    }
}
