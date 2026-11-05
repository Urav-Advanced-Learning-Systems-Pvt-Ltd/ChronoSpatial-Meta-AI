using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SpatialAI
{
    // Navigation surface on UI Toolkit (NavigationUI.uxml + SpatialUI.uss,
    // sortingOrder 200): portrait-first mobile shell with a slim top status
    // bar, a bottom action dock above a 4-tab bar (Scan/Maps/Navigate/Ask),
    // a bottom-sheet route guidance card, a bottom-sheet Telemetry log, and
    // bottom-sheet modal dialogs. The legacy uGUI scene panel is hidden.
    public sealed class SpatialNavigationUI : MonoBehaviour
    {
        UIDocument document;
        VisualElement dock, routePanel, telemetryPanel, dialogOverlay, dialogBody, tabBar;
        Label routeText, dialogTitle;
        Button startBtn, endBtn, pathBtn, saveBtn;
        Button tabScanBtn, tabMapsBtn, tabNavigateBtn, tabAIBtn;
        Label trackingDot, trackingLabel, keyframesChip, landmarksChip, entitiesChip, logText;
        UnityEngine.UI.Text legacyDiagnosticsText;
        SpatialAIController controller;
        SpatialMuseSparkClient museSpark;
        SpatialMap hudMap;
        float nextHudPoll;
        string lastMirroredLog = "";
        bool modalOpen, focusedState;
        Label askStatus;
        string lastAskQuery;
        bool askEventsWired;

        // Derived from the live tree, not just the intent flag: a domain
        // reload serializes the private flag but drops the dynamic dialog
        // content and action registry, and a SafeArea heal re-clones the
        // tree with the overlay hidden. A bare flag would then block every
        // map action behind an invisible modal forever.
        public bool ModalOpen => modalOpen && dialogOverlay != null
            && dialogOverlay.panel != null
            && dialogOverlay.resolvedStyle.display != DisplayStyle.None;
        public bool Focused { get; private set; }
        public event Action PathPressed, StartPressed, EndPressed;

        void Awake()
        {
            controller = FindAnyObjectByType<SpatialAIController>();
            // Retire the uGUI wall + button column: UI Toolkit replaces both.
            var legacyPanel = GameObject.Find("Spatial AI Panel");
            if (legacyPanel != null) legacyPanel.SetActive(false);
            var diagnostics = GameObject.Find("Scan Status");
            if (diagnostics != null)
            {
                legacyDiagnosticsText = diagnostics.GetComponent<UnityEngine.UI.Text>();
                if (diagnostics.transform.parent != null)
                    diagnostics.transform.parent.gameObject.SetActive(false);
            }

            document = SpatialUIToolkitHost.Attach(gameObject, "NavigationUI", 200);
            Bind();
        }

        void Bind()
        {
            if (document == null) return;
            var root = document.rootVisualElement;
            if (root == null || root.childCount == 0) return;
            dock = root.Q<VisualElement>("dock");
            routePanel = root.Q<VisualElement>("routePanel");
            telemetryPanel = root.Q<VisualElement>("telemetryPanel");
            dialogOverlay = root.Q<VisualElement>("dialogOverlay");
            dialogBody = root.Q<VisualElement>("dialogBody");
            dialogTitle = root.Q<Label>("dialogTitle");
            routeText = root.Q<Label>("routeText");
            trackingDot = root.Q<Label>("trackingDot");
            trackingLabel = root.Q<Label>("trackingLabel");
            keyframesChip = root.Q<Label>("keyframesChip");
            landmarksChip = root.Q<Label>("landmarksChip");
            entitiesChip = root.Q<Label>("entitiesChip");
            logText = root.Q<Label>("logText");
            startBtn = root.Q<Button>("startBtn");
            endBtn = root.Q<Button>("endBtn");
            pathBtn = root.Q<Button>("pathBtn");
            saveBtn = root.Q<Button>("saveBtn");
            tabBar = root.Q<VisualElement>("tabBar");
            tabScanBtn = root.Q<Button>("tabScanBtn");
            tabMapsBtn = root.Q<Button>("tabMapsBtn");
            tabNavigateBtn = root.Q<Button>("tabNavigateBtn");
            tabAIBtn = root.Q<Button>("tabAIBtn");

            // Healing re-clones the tree, so the fresh buttons need wiring;
            // the discarded tree takes its old handlers with it, and this
            // Bind only runs when refs are missing/detached (see EnsureBound).
            Wire(root.Q<Button>("scanBtn"), () => { if (controller != null) controller.ScanEnvironment(); });
            Wire(root.Q<Button>("stopBtn"), () => { if (controller != null) controller.StopScan(); });
            Wire(saveBtn, () => { if (controller != null) controller.SaveSpatialMap(); });
            Wire(root.Q<Button>("loadBtn"), () => { if (controller != null) controller.LoadSpatialMap(); });
            var askBtn = root.Q<Button>("detectBtn");
            if (askBtn != null) askBtn.text = "Ask AI";
            Wire(askBtn, ShowAskDialog);
            Wire(pathBtn, () => PathPressed?.Invoke());
            Wire(startBtn, () => StartPressed?.Invoke());
            Wire(endBtn, () => EndPressed?.Invoke());
            Wire(root.Q<Button>("telemetryBtn"), ToggleTelemetry);
            Wire(root.Q<Button>("telemetryCloseBtn"), ToggleTelemetry);
            // Bottom tab bar: thin shortcuts over the same actions so the
            // phone UI reads as pages. Null-safe for older UXML trees.
            Wire(tabScanBtn, () => { if (controller != null) controller.ScanEnvironment(); SelectTab("scan"); });
            Wire(tabMapsBtn, () => { if (controller != null) controller.LoadSpatialMap(); SelectTab("maps"); });
            Wire(tabNavigateBtn, () => { PathPressed?.Invoke(); SelectTab("navigate"); });
            Wire(tabAIBtn, () => { ShowAskDialog(); SelectTab("ai"); });
            if (pathBtn != null) pathBtn.SetEnabled(false);
        }

        static void Wire(Button button, Action action)
        {
            if (button != null) button.clicked += () => action();
        }

        public bool LogOpen { get; private set; }

        // Tab highlight only; every tab reuses the existing dock/dialog
        // action so EditMode dialog-label regression hooks stay unchanged.
        public void SelectTab(string tab)
        {
            SetTabActive(tabScanBtn, tab == "scan");
            SetTabActive(tabMapsBtn, tab == "maps");
            SetTabActive(tabNavigateBtn, tab == "navigate");
            SetTabActive(tabAIBtn, tab == "ai");
        }

        static void SetTabActive(Button tab, bool active)
        {
            if (tab == null) return;
            tab.EnableInClassList("tab-active", active);
        }

        public void ToggleTelemetry()
        {
            if (telemetryPanel == null) return;
            bool show = telemetryPanel.ClassListContains("hidden");
            SpatialUIToolkitHost.Show(telemetryPanel, show);
            // The telemetry card is a bottom sheet over the same anchor as
            // the dock: hide the dock while it is open so actions never
            // ghost through the sheet (PlayMode screenshot finding). A
            // focused route keeps the dock hidden when telemetry closes.
            SpatialUIToolkitHost.Show(dock, !show && !Focused);
            LogOpen = show;
            lastMirroredLog = "";
            if (show) RefreshTelemetryText();
        }

        void RefreshTelemetryText()
        {
            if (logText == null) return;
            string mirrored = legacyDiagnosticsText != null ? legacyDiagnosticsText.text : "";
            if (string.IsNullOrEmpty(mirrored))
                mirrored = "No session details yet. Scan the area to populate mapping diagnostics.";
            lastMirroredLog = legacyDiagnosticsText != null ? legacyDiagnosticsText.text : "";
            logText.text = mirrored;
        }

        void LateUpdate() { PollHud(); }

        void PollHud()
        {
            if (document == null || Time.realtimeSinceStartup < nextHudPoll) return;
            nextHudPoll = Time.realtimeSinceStartup + .25f;
            EnsureBound();
            if (controller == null) controller = FindAnyObjectByType<SpatialAIController>();
            if (hudMap == null) hudMap = controller != null ? controller.Map : FindAnyObjectByType<SpatialMap>();
            bool tracking = controller != null &&
                controller.RuntimeStatus.Native == SpatialRuntimeStatus.NativeStatus.Tracking;
            if (trackingLabel != null) trackingLabel.text = tracking ? "Tracking" : "No tracking";
            if (trackingDot != null)
            {
                trackingDot.RemoveFromClassList(tracking ? "dot-off" : "dot-on");
                trackingDot.AddToClassList(tracking ? "dot-on" : "dot-off");
                float pulse = .65f + .35f * Mathf.Sin(Time.realtimeSinceStartup * 3f);
                trackingDot.style.opacity = tracking ? pulse : 1f;
            }
            if (keyframesChip != null) keyframesChip.text = "Frames: " + (hudMap != null ? hudMap.KeyframeCount : 0);
            if (landmarksChip != null) landmarksChip.text = "Points: " + (hudMap != null ? hudMap.LandmarkCount : 0);
            if (entitiesChip != null) entitiesChip.text = "Objects: " + (hudMap != null ? hudMap.SemanticEntityCount : 0);
            if (saveBtn != null && controller != null)
                saveBtn.SetEnabled(!controller.CaptureInProgress && !controller.CapturePending
                    && hudMap != null && hudMap.KeyframeCount > 0);
            if (telemetryPanel != null && !telemetryPanel.ClassListContains("hidden")
                && legacyDiagnosticsText != null && logText != null
                && legacyDiagnosticsText.text != lastMirroredLog)
            {
                lastMirroredLog = legacyDiagnosticsText.text;
                logText.text = lastMirroredLog;
            }
        }

        public void SetPathReady(bool ready) { if (pathBtn != null) pathBtn.SetEnabled(ready); }

        public void SetFocus(bool focused, string message, bool canStart, string endLabel = "End")
        {
            Focused = focused;
            SpatialUIToolkitHost.Show(routePanel, focused);
            if (focused != focusedState)
            {
                focusedState = focused;
                SpatialUIToolkitHost.Show(dock, !focused);
            }
            if (routeText != null) routeText.text = message;
            SpatialUIToolkitHost.Show(startBtn, canStart);
            if (endBtn != null) endBtn.text = endLabel;
        }

        // ---- Modal dialogs (glass card on a 75% dimmer) ----

        public void CloseModal()
        {
            modalOpen = false;
            askStatus = null;
            dialogActions.Clear();
            SpatialUIToolkitHost.Show(dialogOverlay, false);
            if (dialogBody != null) dialogBody.Clear();
        }

        // ---- Ask AI: floating prompt dialog wired into Muse Spark ----
        // The query is sent to SpatialMuseSparkClient.Ask, which speaks the
        // navigation answer through SpatialSpeech. Presets cover the two
        // canonical queries; the guide preset names the current destination.

        public void ShowAskDialog()
        {
            if (dialogBody == null) return;
            if (museSpark == null) museSpark = FindAnyObjectByType<SpatialMuseSparkClient>();
            if (!askEventsWired && museSpark != null)
            {
                askEventsWired = true;
                museSpark.AdviceReceived += _ => { if (askStatus != null) CloseModal(); };
                museSpark.QueryFailed += reason => { if (askStatus != null) askStatus.text = reason; };
            }
            var panel = BeginDialog("Ask AI");
            MakeLabel(panel, "Ask Muse Spark for navigation guidance. The answer is spoken aloud.", "muted");
            var field = new TextField { value = string.IsNullOrWhiteSpace(lastAskQuery) ? "Guide me to destination" : lastAskQuery };
            field.AddToClassList("field");
            field.maxLength = 280;
            panel.Add(field);
            var presets = new VisualElement();
            presets.AddToClassList("pill-row");
            panel.Add(presets);
            string destination = FindAnyObjectByType<SpatialPlaceUI>() is SpatialPlaceUI places
                && !string.IsNullOrWhiteSpace(places.EntryPointName)
                ? places.EntryPointName : "destination";
            MakeButton(presets, "Guide me", false, () => field.value = "Guide me to " + destination)
                .AddToClassList("pill");
            MakeButton(presets, "Check hazards", false, () => field.value = "Check for path hazards")
                .AddToClassList("pill");
            var chips = CollectLandmarks();
            if (chips.Count > 0)
            {
                MakeLabel(panel, "Landmarks", "muted");
                var grid = new VisualElement();
                grid.AddToClassList("chip-row");
                panel.Add(grid);
                foreach (var chip in chips)
                {
                    string target = chip;
                    MakeButton(grid, target, false, () => AskLandmark(target, field));
                }
            }
            askStatus = MakeLabel(panel, "", "muted");
            MakeButton(panel, "Ask", true, () => SubmitAsk(field.value));
            MakeButton(panel, "Cancel", false, CloseModal);
            field.Focus();
        }

        // One chip per discovered landmark label. Tapping fills the query,
        // asks Muse Spark, and activates the autonomous arrows to it.
        List<string> CollectLandmarks()
        {
            var chips = new List<string>();
            if (controller == null || controller.Map == null) return chips;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in controller.Map.SemanticEntitiesSnapshot())
            {
                if (entity == null || entity.Status == SemanticEntityStatus.Removed) continue;
                if (string.IsNullOrWhiteSpace(entity.label)) continue;
                string label = entity.label.Trim();
                if (seen.Add(label)) chips.Add(label);
            }
            chips.Sort(StringComparer.OrdinalIgnoreCase);
            return chips;
        }

        void AskLandmark(string label, TextField field)
        {
            field.value = "Guide me to " + label;
            var navigator = controller != null ? SpatialAutonomousNavigator.GetOrCreate(controller) : null;
            string navFailure = navigator != null ? navigator.NavigateToLabel(label) : "Navigation is unavailable.";
            SubmitAsk(field.value);
            if (navFailure != null && askStatus != null) askStatus.text = navFailure;
        }

        void SubmitAsk(string query)
        {
            query = query != null ? query.Trim() : "";
            if (askStatus == null) return;
            if (query.Length == 0) { askStatus.text = "Enter a navigation query."; return; }
            if (museSpark == null) { askStatus.text = "Reasoning client unavailable."; return; }
            if (museSpark.Busy) { askStatus.text = "Reasoning query already in progress."; return; }
            lastAskQuery = query;
            askStatus.text = "Asking Muse Spark…";
            museSpark.Ask(query);
        }

        readonly List<(string label, Action action)> dialogActions = new();

        /// <summary>Re-resolve Q&lt;&gt; refs when SafeArea healing has re-cloned the tree.
        /// Detached elements are non-null, so liveness is tested via panel.</summary>
        void EnsureBound()
        {
            if (dock != null && dock.panel != null
                && dialogBody != null && dialogBody.panel != null
                && dialogTitle != null && dialogTitle.panel != null) return;
            Bind();
        }

        VisualElement BeginDialog(string title)
        {
            EnsureBound();
            Debug.Log("[SpatialAI UI] Dialog opened: " + title);
            CloseModal();
            modalOpen = true;
            if (dialogTitle != null) dialogTitle.text = title;
            SpatialUIToolkitHost.Show(dialogOverlay, true);
            if (dialogOverlay != null) dialogOverlay.BringToFront();
            return dialogBody;
        }

        static Label MakeLabel(VisualElement parent, string text, string classes = "body-text")
        {
            var label = new Label(text);
            foreach (var c in classes.Split(' ')) if (c.Length > 0) label.AddToClassList(c);
            parent.Add(label);
            return label;
        }

        Button MakeButton(VisualElement parent, string text, bool primary, Action action)
        {
            var button = new Button(() => action()) { text = text };
            button.AddToClassList("btn");
            if (primary) button.AddToClassList("btn-primary");
            parent.Add(button);
            dialogActions.Add((text, action));
            return button;
        }

        static ScrollView MakeEntries(VisualElement parent)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("dialog-entries");
            parent.Add(scroll);
            return scroll;
        }

        public void NameDialog(string title, string initial, Func<string, string> submit, Action cancel = null, string submitLabel = null)
        {
            var panel = BeginDialog(title);
            var field = new TextField { value = initial ?? "" };
            field.AddToClassList("field");
            field.maxLength = SpatialPlace.MaximumNameLength;
            panel.Add(field);
            var error = MakeLabel(panel, "", "muted");
            error.style.color = new StyleColor(new Color(0.98f, 0.4f, 0.4f));
            MakeButton(panel, submitLabel ?? (title.StartsWith("Save") ? "Save" : "Continue"), true, () =>
            {
                var value = field.value.Trim();
                if (!SpatialNavigationRoute.ValidName(value)) { error.text = "Enter a name, up to 64 characters."; return; }
                var failure = submit(value);
                if (failure == null) CloseModal(); else error.text = failure;
            });
            MakeButton(panel, "Cancel", false, () => { CloseModal(); cancel?.Invoke(); });
            field.Focus();
        }

        public void MapList(List<SpatialMapStorage.Entry> entries, Func<string, string> select,
            Func<string, bool, string> delete = null, Action refresh = null)
        {
            var panel = BeginDialog("Load Map/Path");
            var message = MakeLabel(panel,
                entries.Count == 0 ? "No saved maps yet. Scan an area, then save it."
                    : "Choose a destination, or delete a saved entry.", "muted");
            var scroll = MakeEntries(panel);
            foreach (var entry in entries)
            {
                var captured = entry;
                var row = new VisualElement();
                row.AddToClassList("row");
                Action selectAction = () =>
                {
                    if (captured.failure != null) { message.text = captured.failure; return; }
                    var failure = select(captured.id);
                    if (failure == null) CloseModal(); else message.text = failure;
                };
                var button = new Button(() => selectAction()) { text = captured.ChoiceLabel + (captured.failure == null ? "" : " (unavailable)") };
                button.AddToClassList("btn");
                button.AddToClassList("entry-btn");
                button.style.flexGrow = 1;
                row.Add(button);
                dialogActions.Add((button.text, selectAction));
                if (delete != null)
                {
                    Action removeAction = () => ConfirmDelete(captured, delete, refresh ?? (() => MapList(entries, select, delete, refresh)));
                    var remove = new Button(() => removeAction()) { text = "Delete" };
                    remove.AddToClassList("btn");
                    remove.style.width = 110;
                    row.Add(remove);
                    dialogActions.Add(("Delete", removeAction));
                }
                scroll.Add(row);
            }
            var sync = SpatialLanSync.GetOrCreate();
            EnsureSyncHook(sync);
            MakeButton(panel, sync != null && sync.IsHosting ? "Stop Sharing" : "Share Map", false,
                () => ToggleShare(sync, entries, select, delete, refresh));
            MakeButton(panel, "Sync Map", false, () => ShowSyncDialog(sync));
            MakeButton(panel, "Cancel", false, CloseModal);
        }

        // ---- Living Map Sync: Share/Sync live inside Map Management ----
        bool syncHooked;

        void EnsureSyncHook(SpatialLanSync sync)
        {
            if (syncHooked || sync == null) return;
            syncHooked = true;
            sync.SyncCompleted += ShowSyncResult;
        }

        // UI notification for a finished sync (success or failure text).
        // The empty check MUST precede BeginDialog: opening a dialog with
        // nothing in it sets ModalOpen with no buttons, which then blocks
        // every later map action (ShowMapList no-ops on ModalOpen).
        public void ShowSyncResult(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var panel = BeginDialog("Map Sync");
            MakeLabel(panel, message);
            MakeButton(panel, "Close", true, CloseModal);
        }

        void ToggleShare(SpatialLanSync sync, List<SpatialMapStorage.Entry> entries,
            Func<string, string> select, Func<string, bool, string> delete, Action refresh)
        {
            if (sync == null) return;
            if (sync.IsHosting)
            {
                sync.StopHosting();
                MapList(entries, select, delete, refresh);
                return;
            }
            if (!sync.StartHosting())
            {
                ShowSyncResult(sync.LastError ?? "Share failed.");
                return;
            }
            var panel = BeginDialog("Share Map");
            MakeLabel(panel, "Sharing this map on local Wi-Fi.\n\n" + sync.HostUrl
                + "\n\nGuests tap Sync Map on the same network. Hosting keeps running while you use the app.");
            MakeButton(panel, "Stop Sharing", false, () => { sync.StopHosting(); MapList(entries, select, delete, refresh); });
            MakeButton(panel, "Close", true, CloseModal);
        }

        Label syncStatus;

        void ShowSyncDialog(SpatialLanSync sync)
        {
            if (sync == null) return;
            EnsureSyncHook(sync);
            var panel = BeginDialog("Sync Map");
            MakeLabel(panel, "Pull a shared Living Map from a phone on this Wi-Fi.", "muted");
            syncStatus = MakeLabel(panel, sync.IsSyncing ? "Sync already in progress…" : "Idle.");
            if (!sync.IsSyncing)
            {
                MakeButton(panel, "Listen for shared maps", true, () =>
                {
                    if (syncStatus != null) syncStatus.text = "Listening for shared maps…";
                    sync.DiscoverAndSync();
                });
                MakeButton(panel, "Sync via IP…", false, () => ShowSyncIpDialog(sync));
            }
            MakeButton(panel, "Cancel", false, CloseModal);
        }

        void ShowSyncIpDialog(SpatialLanSync sync)
        {
            var panel = BeginDialog("Sync via IP");
            var field = new TextField { value = "" };
            field.AddToClassList("field");
            field.maxLength = 64;
            panel.Add(field);
            syncStatus = MakeLabel(panel, "Enter the host phone's local IP, e.g. 192.168.1.20.");
            MakeButton(panel, "Sync", true, () =>
            {
                if (syncStatus != null) syncStatus.text = "Downloading shared map…";
                sync.SyncFromIp(field.value);
            });
            MakeButton(panel, "Cancel", false, () => ShowSyncDialog(sync));
            field.Focus();
        }

        void ConfirmDelete(SpatialMapStorage.Entry entry, Func<string, bool, string> delete, Action back)
        {
            bool hasPath = !string.IsNullOrEmpty(entry.destination);
            var panel = BeginDialog(hasPath ? "Delete saved path?" : "Delete saved map?");
            MakeLabel(panel, entry.ChoiceLabel + "\n\n" + (hasPath
                ? "Delete path only keeps the scanned map. Delete map and path removes the entire saved entry and its local backups."
                : "Delete map removes this saved scan and its local backups."));
            var error = MakeLabel(panel, "", "muted");
            void Remove(bool pathOnly)
            {
                var failure = delete(entry.id, pathOnly);
                if (failure != null) { error.text = failure; return; }
                CloseModal(); back();
            }
            if (hasPath) MakeButton(panel, "Delete path only", false, () => Remove(true));
            MakeButton(panel, hasPath ? "Delete map and path" : "Delete map", false, () => Remove(false));
            MakeButton(panel, "Cancel", false, () => { CloseModal(); back(); });
        }

        Label searchStatus;
        ScrollView searchChoices;

        public void SearchDialog(Action scanNew, Action cancel)
        {
            var panel = BeginDialog("Find saved Map/Path");
            searchStatus = MakeLabel(panel,
                "Look around. Choose a destination when it appears; checking continues for other paths.", "muted");
            searchChoices = MakeEntries(panel);
            MakeButton(panel, "Scan new area", true, () => { CloseModal(); scanNew(); });
            MakeButton(panel, "Cancel", false, () => { CloseModal(); cancel(); });
        }

        public void SearchMatch(SpatialMapStorage.Entry entry, Func<string, string> load)
        {
            if (searchChoices == null) return;
            var captured = entry;
            Action matchAction = () =>
            {
                var failure = load(captured.id);
                if (failure == null) CloseModal(); else SearchProgress(failure);
            };
            var button = new Button(() => matchAction()) { text = captured.ChoiceLabel };
            button.AddToClassList("btn");
            button.AddToClassList("entry-btn");
            searchChoices.Add(button);
            dialogActions.Add((button.text, matchAction));
        }

        public void SearchProgress(string message) { if (searchStatus != null) searchStatus.text = message; }

        public void SearchResult(string message, IReadOnlyList<SpatialMapStorage.Entry> matches,
            Func<string, string> load, Action scanNew, Action again, Action cancel)
        {
            var panel = BeginDialog(matches == null || matches.Count == 0
                ? "No verified match yet" : "Choose a saved destination");
            searchStatus = MakeLabel(panel, message, "muted");
            searchChoices = MakeEntries(panel);
            if (matches != null) foreach (var entry in matches) SearchMatch(entry, load);
            MakeButton(panel, "Look again", true, () => { CloseModal(); again(); });
            MakeButton(panel, "Scan new area", true, () => { CloseModal(); scanNew(); });
            MakeButton(panel, "Cancel", false, () => { CloseModal(); cancel(); });
        }

        public void UnsavedChanges(Action save, Action discard)
        {
            var panel = BeginDialog("Unsaved map");
            MakeLabel(panel, "Save this scan before continuing, or discard the unsaved changes.");
            MakeButton(panel, "Save first", true, () => { CloseModal(); save(); });
            MakeButton(panel, "Discard changes", false, () => { CloseModal(); discard(); });
            MakeButton(panel, "Cancel", false, CloseModal);
        }

        public void UnfinishedPath(string reason, Action resume, Action mapOnly)
        {
            var panel = BeginDialog("Path needs more detail");
            MakeLabel(panel, reason);
            MakeButton(panel, "Continue recording", true, () => { CloseModal(); resume(); });
            MakeButton(panel, "Keep map without path", false, () => { CloseModal(); mapOnly(); });
        }

        // ---- EditMode regression hooks (UI Toolkit dialogs) ----
        // The deterministic checks run headless and drive dialogs by label
        // instead of pointer input. Invoke the recorded dialog action
        // directly so EditMode runs do not depend on pointer-event dispatch.
        public IReadOnlyList<string> DialogButtonLabels()
        {
            var labels = new List<string>();
            foreach (var entry in dialogActions)
                labels.Add(entry.label);
            return labels;
        }

        public bool PressDialogButton(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            for (var i = 0; i < dialogActions.Count; i++)
            {
                if (dialogActions[i].label != label) continue;
                var action = dialogActions[i].action;
                action?.Invoke();
                return true;
            }
            return false;
        }

        public string DialogFieldText()
        {
            var field = dialogBody != null ? dialogBody.Query<TextField>().First() : null;
            return field != null ? field.value : null;
        }

        public bool SetDialogFieldText(string value)
        {
            var field = dialogBody != null ? dialogBody.Query<TextField>().First() : null;
            if (field == null) return false;
            field.value = value ?? "";
            return true;
        }
    }
}
