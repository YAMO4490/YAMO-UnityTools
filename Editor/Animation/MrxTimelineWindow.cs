using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(YamoMmrpReplayer))]
    internal sealed class YamoMmrpReplayerEditor : UnityEditor.Editor
    {
        private readonly Dictionary<Animator, List<string>> pending = new Dictionary<Animator, List<string>>();
        private readonly HashSet<AnimationTrack> excluded = new HashSet<AnimationTrack>();
        private TimelineAsset currentTimeline;
        private bool busy;
        private int tab;
        private bool customRange;
        private int exportMode;
        private readonly HashSet<(AnimationTrack, AnimationClip)> excludedTakes = new HashSet<(AnimationTrack, AnimationClip)>();
        private double rangeStart, rangeEnd;

        [MenuItem("GameObject/YAMO/YAMO MMRP Replayer")]
        internal static void SelectOrAdd()
        {
            var director = Selection.activeGameObject?.GetComponent<PlayableDirector>();
            if (director == null)
            {
                EditorUtility.DisplayDialog("YAMO MMRP Replayer", "Playable Director가 있는 씬 오브젝트를 선택하세요.", "확인");
                return;
            }

            if (!director.TryGetComponent<YamoMmrpReplayer>(out _))
                Undo.AddComponent<YamoMmrpReplayer>(director.gameObject);
            Selection.activeGameObject = director.gameObject;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(busy))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("director"), new GUIContent("Timeline Director"));
            serializedObject.ApplyModifiedProperties();
            var director = ((YamoMmrpReplayer)target).Director;
            if (director == null || !(director.playableAsset is TimelineAsset timeline))
            {
                EditorGUILayout.HelpBox("Timeline Asset이 연결된 Director를 지정하세요.", MessageType.Info);
                return;
            }

            if (currentTimeline != timeline)
            {
                currentTimeline = timeline;
                excluded.Clear();
                excludedTakes.Clear();
                rangeStart = 0;
                rangeEnd = timeline.duration;
            }

            tab = GUILayout.Toolbar(tab, new[] { "등록", "추출" });
            using (new EditorGUI.DisabledScope(busy))
            {
                if (tab == 0)
                    DrawRegistration(director, timeline);
                else
                    DrawExport(director, timeline);
            }
        }

        private void DrawRegistration(PlayableDirector director, TimelineAsset timeline)
        {
            var tracks = MrxTimelineBipedExport.FindTracks(timeline).ToArray();
            serializedObject.Update();
            var avatars = serializedObject.FindProperty("avatars");
            var registrations = new List<(Animator avatar, List<string> files)>();
            var seen = new HashSet<Animator>();
            for (var index = 0; index < avatars.arraySize; index++)
            {
                var slot = avatars.GetArrayElementAtIndex(index);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(slot, new GUIContent($"아바타 {index + 1}"));
                        if (GUILayout.Button("제외", GUILayout.Width(48)))
                        {
                            if (slot.objectReferenceValue is Animator removed)
                                pending.Remove(removed);
                            slot.objectReferenceValue = null;
                            avatars.DeleteArrayElementAtIndex(index);
                            break;
                        }
                    }

                    var animator = slot.objectReferenceValue as Animator;
                    if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                    {
                        EditorGUILayout.HelpBox("유효한 Humanoid Animator를 지정하세요.", MessageType.Info);
                        continue;
                    }
                    if (!seen.Add(animator))
                    {
                        EditorGUILayout.HelpBox("위에 등록된 동일 아바타 영역에 MRX를 추가하세요.", MessageType.Info);
                        continue;
                    }

                    if (!pending.TryGetValue(animator, out var files))
                        pending.Add(animator, files = new List<string>());
                    registrations.Add((animator, files));
                    for (var indexFile = 0; indexFile < files.Count; indexFile++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(new GUIContent(Path.GetFileName(files[indexFile]), files[indexFile]));
                            if (GUILayout.Button("제외", GUILayout.Width(48)))
                            {
                                files.RemoveAt(indexFile);
                                break;
                            }
                        }
                    }

                    if (!busy)
                        DrawDropArea(files);
                    if (GUILayout.Button("MRX 파일 추가"))
                    {
                        var path = EditorUtility.OpenFilePanel("MMRP 녹화", "", "mrx");
                        if (!string.IsNullOrEmpty(path))
                            files.Add(path);
                    }
                }
            }

            if (GUILayout.Button("아바타 추가"))
                avatars.GetArrayElementAtIndex(avatars.arraySize++).objectReferenceValue = null;
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.HelpBox("MRX마다 파일명으로 그룹과 모션 트랙이 생성되며, 변환 에셋은 씬 옆 MMRP 폴더에 저장됩니다. 각 클립은 재생 헤드에서 시작하며 같은 아바타의 후속 MRX 그룹은 음소거됩니다.", MessageType.Info);
            using (new EditorGUI.DisabledScope(registrations.All(entry => entry.files.Count == 0)))
                if (GUILayout.Button("Timeline에 등록"))
                    Register(director, registrations);
            var footIkClips = tracks.SelectMany(track => track.GetClips())
                .Select(clip => clip.asset as AnimationPlayableAsset)
                .Where(asset => asset != null && asset.applyFootIK && (MrxTimelineSource.RawFbx(asset.clip) != null || HasRawHands(asset.clip))).Distinct().ToArray();
            if (footIkClips.Length > 0 && GUILayout.Button("기존 MRX 클립의 Foot IK 끄기"))
            {
                Undo.RecordObjects(footIkClips, "Disable Foot IK on MRX clips");
                foreach (var asset in footIkClips)
                {
                    asset.applyFootIK = false;
                    EditorUtility.SetDirty(asset);
                }
                AssetDatabase.SaveAssetIfDirty(timeline);
                director.RebuildGraph();
            }

            DrawMasterAudio(director, timeline);
        }

        private static bool HasRawHands(AnimationClip clip)
        {
            var path = AssetDatabase.GetAssetPath(clip);
            return !string.IsNullOrEmpty(path) &&
                   AssetDatabase.LoadAllAssetsAtPath(path).OfType<MrxRawHands>().Any();
        }
        private static void DrawDropArea(List<string> files)
        {
            var area = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            GUI.Box(area, "MRX 파일을 여기에 끌어다 놓으세요");
            var evt = Event.current;
            if (!area.Contains(evt.mousePosition) ||
                (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform))
                return;

            var paths = DragAndDrop.paths.Where(path =>
                string.Equals(Path.GetExtension(path), ".mrx", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path)).ToArray();
            DragAndDrop.visualMode = paths.Length == 0
                ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform && paths.Length > 0)
            {
                DragAndDrop.AcceptDrag();
                files.AddRange(paths.Select(Path.GetFullPath));
            }

            evt.Use();
        }
        private async void Register(PlayableDirector director, List<(Animator avatar, List<string> files)> registrations)
        {
            var start = Math.Max(0, director.time);
            busy = true;
            try
            {
                var folder = GetOrCreateSceneFolder(director);
                foreach (var entry in registrations)
                {
                    while (entry.files.Count != 0)
                    {
                        await ImportAsync(director, entry.avatar, entry.files[0], folder, start, Vector3.zero);
                        entry.files.RemoveAt(0);
                    }
                }
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogException(error);
                EditorUtility.DisplayDialog("MRX 등록 실패", error.GetBaseException().Message, "확인");
            }
            finally
            {
                busy = false;
                if (this != null)
                    Repaint();
            }
        }

        internal static string GetOrCreateSceneFolder(PlayableDirector director)
        {
            var scenePath = director.gameObject.scene.path;
            if (string.IsNullOrEmpty(scenePath) || !scenePath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Director가 속한 씬을 먼저 프로젝트 Assets에 저장하세요.");

            var sceneFolder = Path.GetDirectoryName(scenePath).Replace('\\', '/');
            var assetFolder = sceneFolder + "/MMRP";
            if (!AssetDatabase.IsValidFolder(assetFolder))
            {
                var guid = AssetDatabase.CreateFolder(sceneFolder, "MMRP");
                if (AssetDatabase.GUIDToAssetPath(guid) != assetFolder)
                    throw new IOException("씬 폴더에 MMRP 폴더를 만들 수 없습니다: " + assetFolder);
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetFolder));
        }
        private void DrawExport(PlayableDirector director, TimelineAsset timeline)
        {
            var tracks = MrxTimelineBipedExport.FindTracks(timeline).ToArray();
            exportMode = GUILayout.Toolbar(exportMode, new[] { "Timeline 편집본", "MRX 전체" });
            if (exportMode == 1)
            {
                DrawWholeTake(director, tracks);
                return;
            }
            foreach (var track in tracks)
            {
                var binding = director.GetGenericBinding(track);
                var label = $"{track.name}  ·  {(binding == null ? "미바인딩" : binding.name)}";
                if (track.mutedInHierarchy)
                {
                    EditorGUILayout.LabelField(label + " (음소거)");
                    continue;
                }
                if (EditorGUILayout.ToggleLeft(label, !excluded.Contains(track)))
                    excluded.Remove(track);
                else
                    excluded.Add(track);
            }

            customRange = EditorGUILayout.Toggle("출력 범위 지정", customRange);
            if (customRange)
            {
                rangeStart = EditorGUILayout.DoubleField("시작 (초)", rangeStart);
                rangeEnd = EditorGUILayout.DoubleField("끝 (초)", rangeEnd);
            }
            else
            {
                rangeStart = 0;
                rangeEnd = timeline.duration;
                EditorGUILayout.LabelField("Timeline 범위", $"0 – {rangeEnd:0.###}초");
            }

            var selected = tracks.Where(track => !track.mutedInHierarchy && !excluded.Contains(track)).ToArray();
            using (new EditorGUI.DisabledScope(selected.Length == 0))
                if (GUILayout.Button("Biped FBX 일괄 추출"))
                    ExportBiped(director, selected);
        }

        private void DrawWholeTake(PlayableDirector director, AnimationTrack[] tracks)
        {
            var takes = tracks.SelectMany(track => track.GetClips().OrderBy(clip => clip.start)
                    .Where(clip => !string.IsNullOrEmpty(clip.GetRawFbx()))
                    .GroupBy(clip => ((AnimationPlayableAsset)clip.asset).clip)
                    .Select(group => (track, animation: group.Key, clip: group.First(),
                        avatar: director.GetGenericBinding(track) as Animator))).ToArray();
            if (takes.Length == 0)
            {
                EditorGUILayout.HelpBox("FBX 방식으로 등록된 MRX가 필요합니다.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox("원본 전체 길이에 트랙과 첫 분할 클립의 위치·회전 오프셋을 적용합니다. 음소거된 테이크도 선택할 수 있습니다.", MessageType.Info);
            var selected = new List<TimelineClip>();
            foreach (var group in takes.GroupBy(item => item.avatar))
            {
                EditorGUILayout.LabelField(group.Key == null ? "미바인딩" : group.Key.name, EditorStyles.boldLabel);
                using (new EditorGUI.IndentLevelScope())
                foreach (var take in group)
                {
                    var key = (take.track, take.animation);
                    var enabled = group.Key != null && group.Key.avatar != null && group.Key.avatar.isValid && group.Key.avatar.isHuman;
                    var label = $"{take.track.name} · {take.clip.displayName} ({take.animation.length:0.###}초)";
                    using (new EditorGUI.DisabledScope(!enabled))
                    {
                        if (EditorGUILayout.ToggleLeft(label, enabled && !excludedTakes.Contains(key)))
                        {
                            excludedTakes.Remove(key);
                            selected.Add(take.clip);
                        }
                        else if (enabled)
                            excludedTakes.Add(key);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(selected.Count == 0))
                if (GUILayout.Button("선택 MRX 전체 일괄 추출"))
                    RunExport(folder => MrxTimelineBipedExport.ExportWholeTakes(director, selected, folder));
        }
        private void ExportBiped(PlayableDirector director, AnimationTrack[] tracks) =>
            RunExport(folder => MrxTimelineBipedExport.Export(director, folder, tracks, rangeStart, rangeEnd));

        private static void RunExport(Func<string, string[]> export)
        {
            var folder = EditorUtility.OpenFolderPanel("Biped FBX 출력 폴더", "", "");
            if (string.IsNullOrEmpty(folder))
                return;
            try
            {
                var files = export(folder);
                EditorUtility.DisplayDialog("MRX FBX 추출", string.Join("\n", files), "확인");
            }
            catch (OperationCanceledException)
            {
                UnityEngine.Debug.Log("MRX FBX 추출이 취소되었습니다.");
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogException(error);
                EditorUtility.DisplayDialog("MRX FBX 추출 실패", error.GetBaseException().Message, "확인");
            }
        }
        internal static async Task<TimelineClip> ImportAsync(PlayableDirector target, Animator animator, string source, string folder,
            double start, Vector3 offset)
        {
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("유효한 Humanoid Avatar가 지정된 Animator를 선택하세요.");
            if (!(target.playableAsset is TimelineAsset timeline) || !AssetDatabase.Contains(timeline))
                throw new InvalidOperationException("저장된 Timeline Asset을 지정하세요.");
            if (double.IsNaN(start) || double.IsInfinity(start) || start < 0)
                throw new ArgumentOutOfRangeException(nameof(start), "시작 시점은 0초 이상이어야 합니다.");

            var assetRoot = Path.GetFullPath(Application.dataPath);
            var relative = Path.GetRelativePath(assetRoot, Path.GetFullPath(folder)).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                throw new ArgumentException("프로젝트 Assets 아래 기존 폴더를 선택하세요.");
            var output = relative == "." ? "Assets" : "Assets/" + relative;
            if (!AssetDatabase.IsValidFolder(output))
                throw new ArgumentException("프로젝트 Assets 아래 기존 폴더를 선택하세요.");
            var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var work = Path.GetFullPath(Path.Combine("Codex", date, "Mrx", Guid.NewGuid().ToString("N")));
            var bundle = Path.Combine(work, "bundle");
            var body = Path.Combine(work, "body.jsonl");
            var wav = Path.Combine(work, "audio.wav");
            var projector = MrxRuntime.InitializeOnMainThread();
            var audio = await Task.Run(() =>
            {
                Directory.CreateDirectory(work);
                MrxNativeReader.Export(source, bundle);
                var metadata = JObject.Parse(File.ReadAllText(Path.Combine(bundle, "archive.json")));
                var audioMetadata = metadata["audio"] as JObject;
                if (audioMetadata != null)
                {
                    var ratio = (double?)audioMetadata["clock"]?["ratio"] ?? 1.0;
                    MrxNativeAudio.DecodePreviewAudio(Path.Combine(bundle, "audio.ogg"), wav,
                        System.Threading.CancellationToken.None, clockRatio: ratio);
                }

                var reader = MrxRuntime.FindFile("StreamingleMrxStreaming2.dll");
                var pythonHome = MrxNativeReader.SelectPythonHome(
                    Environment.GetEnvironmentVariable("YAMO_MRX_PYTHON_HOME"), MrxRuntime.PythonHomes());
                Run(Path.Combine(pythonHome, "python.exe"), "-B", projector,
                    Path.Combine(Path.GetDirectoryName(reader), "MrxModules"), bundle, body);
                return audioMetadata;
            });

            if (target == null || animator == null || target.playableAsset != timeline)
                throw new InvalidOperationException("가져오는 동안 Timeline 또는 Animator가 변경되었습니다.");
            var name = Path.GetFileNameWithoutExtension(source);
            var animation = MrxHumanoidClip.Build(body, output, name);

            Undo.RegisterCompleteObjectUndo(target, "Add MRX take");
            Undo.RegisterCompleteObjectUndo(timeline, "Add MRX take");
            var hasTrack = MrxTimelineBipedExport.FindTracks(timeline)
                .Any(track => target.GetGenericBinding(track) == animator);
            var group = timeline.CreateTrack<GroupTrack>(null, name);
            var motionTrack = timeline.CreateTrack<AnimationTrack>(group, name);
            group.muted = hasTrack;
            motionTrack.trackOffset = TrackOffset.ApplyTransformOffsets;
            target.SetGenericBinding(motionTrack, animator);
            Undo.RegisterCompleteObjectUndo(motionTrack, "Add MRX motion");
            var motion = motionTrack.CreateClip(animation);
            motion.start = start;
            motion.displayName = name;
            var playable = (AnimationPlayableAsset)motion.asset;
            playable.removeStartOffset = false;
            playable.applyFootIK = false;
            playable.position = animator.transform.parent == null
                ? offset : animator.transform.parent.InverseTransformVector(offset);
            EditorUtility.SetDirty(motionTrack);
            EditorUtility.SetDirty(playable);

            if (audio != null)
            {
                var audioPath = AssetDatabase.GenerateUniqueAssetPath($"{output}/{name}.wav");
                File.Copy(wav, audioPath);
                AssetDatabase.ImportAsset(audioPath, ImportAssetOptions.ForceSynchronousImport);
                var audioClip = AssetDatabase.LoadAssetAtPath<AudioClip>(audioPath);
                if (audioClip == null)
                    throw new InvalidOperationException("MRX WAV import failed.");
                var tracks = MasterTracks(timeline);
                var audioTrack = timeline.CreateTrack<AudioTrack>(group, AudioPrefix + name);
                var clip = audioTrack.CreateClip(audioClip);
                var audioStart = (double)audio["sessionTimeNs"] / 1e9;
                clip.start = Math.Max(0, start + audioStart);
                clip.clipIn = Math.Max(0, -(start + audioStart));
                clip.duration = Math.Max(0.001, audioClip.length - clip.clipIn);
                audioTrack.muted = tracks.Length != 0;
                EditorUtility.SetDirty(audioTrack);
            }

            EditorUtility.SetDirty(timeline);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssetIfDirty(timeline);
            target.RebuildGraph();
            return motion;
        }

        private const string AudioPrefix = "MRX Audio · ";

        private static AudioTrack[] MasterTracks(TimelineAsset timeline) =>
            timeline.GetOutputTracks().OfType<AudioTrack>().Where(track => track.name.StartsWith(AudioPrefix, StringComparison.Ordinal)).ToArray();

        private static void DrawMasterAudio(PlayableDirector director, TimelineAsset timeline)
        {
            var tracks = MasterTracks(timeline);
            if (tracks.Length == 0)
                return;

            var current = Array.FindIndex(tracks, track => !track.muted);
            var names = new[] { "모두 음소거" }.Concat(tracks.Select(track => track.name)).ToArray();
            EditorGUI.BeginChangeCheck();
            var selected = EditorGUILayout.Popup("마스터 MRX 오디오", current + 1, names) - 1;
            if (!EditorGUI.EndChangeCheck())
                return;

            Undo.RecordObjects(tracks, "Select MRX master audio");
            for (var index = 0; index < tracks.Length; index++)
            {
                tracks[index].muted = index != selected;
                EditorUtility.SetDirty(tracks[index]);
            }
            AssetDatabase.SaveAssetIfDirty(timeline);
            director.RebuildGraph();
        }

        private static void Run(string executable, params string[] arguments)
        {
            if (!File.Exists(executable))
                throw new FileNotFoundException("실행 파일을 지정하세요.", executable);
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = string.Join(" ", arguments.Select(argument =>
                    "\"" + argument.Replace("\"", "\\\"").TrimEnd('\\') + "\""))
            };
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"{Path.GetFileName(executable)}: {error.Trim()}");
            }
        }
    }
}
