using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Facial Animation Baker
    ///
    /// .anim 페이셜 애니메이션(블렌드셰이프가 많아 용량이 큰 YAML 클립)을
    /// .fbx 로 베이크해 용량을 줄이는 에디터 도구.
    ///
    /// 설계 요점:
    ///   - 페이셜 클립은 여러 캐릭터가 돌려 쓰므로 "원본 캐릭터"에 의존하지 않는다.
    ///   - 대신 클립의 EditorCurveBinding 으로부터 최소 셸(Shell) 하이어라키를
    ///     자동 구성한다: 필요한 transform 경로 + 컴포넌트 + 블렌드셰이프 이름.
    ///   - 이 셸에 임시 AnimatorController(해당 클립)를 연결해 FBX Exporter 로
    ///     내보내면, 결과 FBX 는 클립의 원래 path / propertyName 바인딩을
    ///     그대로 유지한다. → 어떤 캐릭터에 적용해도 원본 anim 과 동일한 경로
    ///     매칭이 성립 (계층구조 참조가 깨지지 않음).
    ///   - 셸과 임시 자산은 try/finally 로 반드시 정리.
    ///
    /// 의존: Unity FBX Exporter (com.unity.formats.fbx) — 리플렉션으로 선택적 사용.
    /// </summary>
    public class FacialAnimationBaker : EditorWindow
    {
        private readonly List<AnimationClip> clips = new List<AnimationClip>();
        private string outputFolderPath = DefaultOutputFolder;
        private bool overwriteExisting = true;
        private bool pingAfterExport = true;
        private int keyframeStride = 3;   // 1 = 모든 키 유지, 3 = 3프레임당 1키

        private Vector2 clipScroll;
        private Vector2 logScroll;
        private readonly List<string> logLines = new List<string>();

        /// <summary>클립별 path 선택 상태 캐시.</summary>
        private class ClipSelection
        {
            public string[] paths;                 // 정렬된 고유 path 목록 ("" == 루트)
            public Dictionary<string, bool> include = new Dictionary<string, bool>(StringComparer.Ordinal);
            public bool foldout = true;
            public Vector2 scroll;
        }
        private readonly Dictionary<AnimationClip, ClipSelection> selections = new Dictionary<AnimationClip, ClipSelection>();

        private const string DefaultOutputFolder = "Assets/Facial";

        private const string FbxExporterAssemblyName = "Unity.Formats.Fbx.Editor";
        private const string FbxExporterTypeName    = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        private const string TempWorkFolder         = "Assets/__YAMO_FacialBake_Temp";
        private const string TempControllerPath     = "Assets/__YAMO_FacialBake_Temp/__ctrl.controller";
        private const string ShellRootName          = "__YAMO_FacialBake_Shell";

        [MenuItem("Tools/YAMO/Animation/Facial Animation Baker")]
        public static void ShowWindow()
        {
            var w = GetWindow<FacialAnimationBaker>("Facial Anim Baker");
            w.minSize = new Vector2(440, 460);
        }

        // ---------------------------------------------------------------------
        // GUI
        // ---------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Facial Animation → FBX Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                ".anim 페이셜 클립을 FBX 로 베이크해 용량을 줄입니다.\n" +
                "클립의 바인딩(path / propertyName / 블렌드셰이프 이름)을 그대로 보존하므로,\n" +
                "결과 FBX 는 기존 .anim 과 동일하게 어떤 캐릭터에도 적용할 수 있습니다.\n" +
                "원본 캐릭터는 필요하지 않습니다.",
                MessageType.Info);

            // 클립 리스트 + 각 클립의 path 선택
            EditorGUILayout.LabelField($"Clips ({clips.Count})", EditorStyles.boldLabel);
            using (var s = new EditorGUILayout.ScrollViewScope(clipScroll, GUILayout.MinHeight(160), GUILayout.MaxHeight(380)))
            {
                clipScroll = s.scrollPosition;
                for (int i = 0; i < clips.Count; i++)
                {
                    DrawClipEntry(i);
                    EditorGUILayout.Space(2);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Add Slot")) clips.Add(null);
                if (GUILayout.Button("Add Selected .anim")) AddSelectedClips();
                if (GUILayout.Button("Clear")) { clips.Clear(); selections.Clear(); }
            }

            EditorGUILayout.Space(4);

            // 출력 폴더 — 문자열 + Browse 다이얼로그
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Output Folder");
                outputFolderPath = EditorGUILayout.TextField(outputFolderPath);
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    var startFolder = GetExistingParent(outputFolderPath);
                    var picked = EditorUtility.OpenFolderPanel("Select Output Folder", startFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        var rel = ToProjectRelative(picked);
                        if (rel != null) outputFolderPath = rel;
                        else EditorUtility.DisplayDialog("경로 오류", "Output 폴더는 프로젝트의 Assets/ 하위여야 합니다.", "OK");
                    }
                }
                if (GUILayout.Button("Reset", GUILayout.Width(60)))
                    outputFolderPath = DefaultOutputFolder;
            }

            overwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", overwriteExisting);
            pingAfterExport   = EditorGUILayout.Toggle("Ping After Export",  pingAfterExport);

            // 키프레임 데시메이션 — 페이셜 클립 용량의 결정적 요인
            using (new EditorGUILayout.HorizontalScope())
            {
                keyframeStride = EditorGUILayout.IntField(
                    new GUIContent("Keyframe Stride",
                        "N개 프레임마다 1개의 키만 남깁니다. 1=원본 유지, 3=1,4,7…번 프레임만 유지(2,3,5,6… 삭제)."),
                    keyframeStride);
                if (keyframeStride < 1) keyframeStride = 1;
                GUILayout.Label(keyframeStride == 1 ? "(원본 유지)" : $"({Mathf.RoundToInt(100f / keyframeStride)}% 키 유지)",
                    EditorStyles.miniLabel, GUILayout.Width(110));
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(!HasAnyClip()))
            {
                if (GUILayout.Button("Bake to FBX", GUILayout.Height(28)))
                    BakeAll();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            using (var s = new EditorGUILayout.ScrollViewScope(logScroll, GUILayout.MinHeight(140)))
            {
                logScroll = s.scrollPosition;
                foreach (var line in logLines)
                    EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
            }
            if (GUILayout.Button("Clear Log", GUILayout.Width(100))) logLines.Clear();
        }

        private bool HasAnyClip() => clips.Any(c => c != null);

        private void AddSelectedClips()
        {
            foreach (var o in Selection.objects)
                if (o is AnimationClip c && !clips.Contains(c)) clips.Add(c);
        }

        private void DrawClipEntry(int i)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var prev = clips[i];
                    clips[i] = (AnimationClip)EditorGUILayout.ObjectField(clips[i], typeof(AnimationClip), false);
                    if (prev != clips[i] && prev != null) selections.Remove(prev);

                    if (GUILayout.Button("×", GUILayout.Width(24)))
                    {
                        if (clips[i] != null) selections.Remove(clips[i]);
                        clips.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }

                var clip = clips[i];
                if (clip == null) return;

                var sel = GetOrScanSelection(clip);

                // 헤더 — foldout + 카운트 + 일괄 버튼
                using (new EditorGUILayout.HorizontalScope())
                {
                    int total = sel.paths.Length;
                    int on = sel.include.Count(kv => kv.Value);
                    sel.foldout = EditorGUILayout.Foldout(sel.foldout, $"Objects in clip  ({on}/{total} selected)", true);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("All", GUILayout.Width(40)))
                        foreach (var p in sel.paths) sel.include[p] = true;
                    if (GUILayout.Button("None", GUILayout.Width(50)))
                        foreach (var p in sel.paths) sel.include[p] = false;
                    if (GUILayout.Button("Face only", GUILayout.Width(80)))
                    {
                        foreach (var p in sel.paths)
                            sel.include[p] = string.Equals(LeafName(p), "Face", StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!sel.foldout) return;

                // 스크롤 가능한 토글 목록
                using (var sv = new EditorGUILayout.ScrollViewScope(sel.scroll, GUILayout.MinHeight(60), GUILayout.MaxHeight(180)))
                {
                    sel.scroll = sv.scrollPosition;
                    foreach (var p in sel.paths)
                    {
                        var label = string.IsNullOrEmpty(p) ? "<root>" : p;
                        sel.include[p] = EditorGUILayout.ToggleLeft(label, sel.include[p]);
                    }
                }
            }
        }

        private ClipSelection GetOrScanSelection(AnimationClip clip)
        {
            if (selections.TryGetValue(clip, out var sel) && sel.paths != null) return sel;
            sel = new ClipSelection();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in AnimationUtility.GetCurveBindings(clip))             paths.Add(b.path ?? "");
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip)) paths.Add(b.path ?? "");
            sel.paths = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            // 기본: 'Face' 리프 노드만 체크(사용자의 일반적 요구 반영), 없으면 전체 체크
            bool anyFace = sel.paths.Any(p => string.Equals(LeafName(p), "Face", StringComparison.OrdinalIgnoreCase));
            foreach (var p in sel.paths)
                sel.include[p] = anyFace
                    ? string.Equals(LeafName(p), "Face", StringComparison.OrdinalIgnoreCase)
                    : true;
            selections[clip] = sel;
            return sel;
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        private static string GetExistingParent(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return Application.dataPath;
            var abs = Path.GetFullPath(rel);
            while (!Directory.Exists(abs))
            {
                var parent = Path.GetDirectoryName(abs);
                if (string.IsNullOrEmpty(parent) || parent == abs) break;
                abs = parent;
            }
            return Directory.Exists(abs) ? abs : Application.dataPath;
        }

        private static string ToProjectRelative(string abs)
        {
            var dataPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            var full = Path.GetFullPath(abs).Replace('\\', '/');
            if (!full.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + full.Substring(dataPath.Length);
        }

        private static void EnsureFolderRecursive(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder)) return;
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (assetFolder == "Assets" || AssetDatabase.IsValidFolder(assetFolder)) return;
            var parent = Path.GetDirectoryName(assetFolder)?.Replace('\\', '/');
            var leaf   = Path.GetFileName(assetFolder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
            EnsureFolderRecursive(parent);
            if (!AssetDatabase.IsValidFolder(assetFolder))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        // ---------------------------------------------------------------------
        // Bake pipeline
        // ---------------------------------------------------------------------

        private void BakeAll()
        {
            logLines.Clear();

            var invoker = FbxExportInvoker.Resolve();
            if (invoker == null)
            {
                EditorUtility.DisplayDialog(
                    "FBX Exporter 필요",
                    "Unity FBX Exporter 패키지(com.unity.formats.fbx)가 설치되지 않았습니다.\n\n" +
                    "Window > Package Manager > + > Add package by name...\n에서 com.unity.formats.fbx 를 설치해 주세요.",
                    "OK");
                Log("✗ FBX Exporter 미설치 또는 호환되지 않는 버전");
                return;
            }
            Log($"FBX Exporter: {invoker.Describe()}");
            Debug.Log("[FacialAnimBaker] " + invoker.DumpOptionsMembers());

            string folderPath = (outputFolderPath ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folderPath)) folderPath = DefaultOutputFolder;
            if (!folderPath.StartsWith("Assets", StringComparison.Ordinal))
            {
                Log($"✗ Output 경로는 Assets/ 하위여야 합니다: {folderPath}");
                return;
            }
            EnsureFolderRecursive(folderPath);
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                Log($"✗ Output 폴더 생성 실패: {folderPath}");
                return;
            }
            Log($"출력 폴더: {folderPath}");

            int ok = 0, fail = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    if (clip == null) continue;
                    EditorUtility.DisplayProgressBar("Facial Anim Bake", clip.name, (float)i / Mathf.Max(1, clips.Count));
                    if (BakeOne(clip, folderPath, invoker)) ok++; else fail++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                // 남아 있을 수 있는 임시 작업 폴더 제거
                if (AssetDatabase.IsValidFolder(TempWorkFolder))
                    AssetDatabase.DeleteAsset(TempWorkFolder);
                AssetDatabase.Refresh();
            }

            Log($"완료 — 성공 {ok} / 실패 {fail}");
        }

        private bool BakeOne(AnimationClip clip, string folderPath, FbxExportInvoker invoker)
        {
            string fileName = SanitizeFileName(clip.name) + ".fbx";
            string fbxPath  = Path.Combine(folderPath, fileName).Replace('\\', '/');
            if (!overwriteExisting && File.Exists(fbxPath))
            {
                Log($"- 건너뜀(이미 존재): {fbxPath}");
                return false;
            }

            GameObject shell = null;
            AnimatorController tempController = null;
            AnimationClip filteredClip = null;
            string filteredClipPath = null;
            var createdMeshes = new List<Mesh>();
            bool success = false;

            try
            {
                // 0) 선택된 path 만 남긴 필터 클립 생성
                var sel = GetOrScanSelection(clip);
                var allowed = new HashSet<string>(
                    sel.include.Where(kv => kv.Value).Select(kv => kv.Key),
                    StringComparer.Ordinal);
                if (allowed.Count == 0)
                {
                    Log($"✗ [{clip.name}] 선택된 오브젝트가 없어 건너뜀");
                    return false;
                }
                // 임시 anim 에셋의 파일명 = 원본 클립명 으로 맞춰야 FBX 안의
                // AnimationClip take 이름이 원본명으로 찍힌다.
                // Assets/ 루트를 어지럽히지 않도록 전용 서브폴더에 담는다.
                EnsureFolderRecursive(TempWorkFolder);
                filteredClipPath = $"{TempWorkFolder}/{SanitizeFileName(clip.name)}.anim";
                filteredClip = BuildFilteredClip(clip, allowed, filteredClipPath, Mathf.Max(1, keyframeStride));
                int dropped = (AnimationUtility.GetCurveBindings(clip).Length + AnimationUtility.GetObjectReferenceCurveBindings(clip).Length)
                            - (AnimationUtility.GetCurveBindings(filteredClip).Length + AnimationUtility.GetObjectReferenceCurveBindings(filteredClip).Length);
                Log($"[{clip.name}] 선택 오브젝트 {allowed.Count} 개 / 제외된 커브 {dropped} 개");

                // 1) 필터 클립의 바인딩으로부터 셸 구성
                shell = BuildShell(filteredClip, createdMeshes);
                Log($"[{clip.name}] 셸 구성: transforms={CountTransforms(shell)}, meshes={createdMeshes.Count}");

                // 2) 임시 컨트롤러 + Animator 연결
                if (File.Exists(TempControllerPath)) AssetDatabase.DeleteAsset(TempControllerPath);
                tempController = AnimatorController.CreateAnimatorControllerAtPathWithClip(TempControllerPath, filteredClip);
                var animator = shell.GetComponent<Animator>();
                if (animator == null) animator = shell.AddComponent<Animator>();
                animator.runtimeAnimatorController = tempController;
                animator.applyRootMotion = false;
                animator.enabled = true;

                // 3) FBX 내보내기 (애니메이션 포함 옵션 지정)
                Log($"[{clip.name}] 내보내기 → {fbxPath}");
                invoker.Export(fbxPath, shell, shell.transform);

                if (!File.Exists(fbxPath))
                    throw new Exception("FBX 파일이 생성되지 않았습니다.");

                success = true;
            }
            catch (Exception e)
            {
                Log($"✗ [{clip.name}] 실패: {e.GetBaseException().Message}");
                Debug.LogException(e);
            }
            finally
            {
                // 4) 정리 — 항상 실행
                if (shell != null) DestroyImmediate(shell);
                if (File.Exists(TempControllerPath)) AssetDatabase.DeleteAsset(TempControllerPath);
                if (!string.IsNullOrEmpty(filteredClipPath) && File.Exists(filteredClipPath))
                    AssetDatabase.DeleteAsset(filteredClipPath);
                foreach (var m in createdMeshes)
                    if (m != null) DestroyImmediate(m);
            }

            if (success)
            {
                AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer != null)
                {
                    importer.importAnimation = true;
                    importer.animationType = ModelImporterAnimationType.Generic;
                    // Anim. Compression = Off — 키프레임 정밀도 유지
                    importer.animationCompression = ModelImporterAnimationCompression.Off;

                    // 클립 이름을 FBX 파일명(= 원본 클립명)과 동일하게 고정
                    var targetClipName = Path.GetFileNameWithoutExtension(fbxPath);
                    var defaults = importer.defaultClipAnimations;
                    if (defaults != null && defaults.Length > 0)
                    {
                        for (int k = 0; k < defaults.Length; k++)
                            defaults[k].name = defaults.Length == 1
                                ? targetClipName
                                : $"{targetClipName}_{k}";
                        importer.clipAnimations = defaults;
                    }

                    importer.SaveAndReimport();
                }
                Log($"✓ [{clip.name}] 완료: {fbxPath}");
                if (pingAfterExport)
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(fbxPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
            }
            return success;
        }

        /// <summary>
        /// stride 간격으로 키를 솎아낸 새 AnimationCurve 를 반환.
        /// 인덱스 0, stride, 2*stride, … 의 키를 유지하고, 마지막 키는 클립 길이 보존을 위해 항상 유지.
        /// 키 사이 보간은 원본 in/out slope 를 그대로 승계 — 페이셜 곡선은 값이 완만해 청감·시감 차이가 미미함.
        /// </summary>
        private static AnimationCurve DecimateCurve(AnimationCurve src, int stride)
        {
            var keys = src.keys;
            if (keys == null || keys.Length <= 2 || stride <= 1) return src;

            var kept = new List<Keyframe>(keys.Length / stride + 2);
            for (int i = 0; i < keys.Length; i++)
            {
                if (i % stride == 0) kept.Add(keys[i]);
            }
            // 마지막 키 보장 (클립 길이 유지)
            if (kept.Count == 0 || kept[kept.Count - 1].time < keys[keys.Length - 1].time)
                kept.Add(keys[keys.Length - 1]);

            var outCurve = new AnimationCurve(kept.ToArray());
            outCurve.preWrapMode  = src.preWrapMode;
            outCurve.postWrapMode = src.postWrapMode;
            return outCurve;
        }

        /// <summary>
        /// 선택된 path 의 커브만 복사한 임시 AnimationClip 자산을 생성.
        /// 이렇게 하면 BuildShell 과 FBX Exporter 양쪽에서 바인딩이 일관되게 줄어들어
        /// 경고 없이 깔끔히 베이크된다. 루프 노드 속성(m_SampleRate, wrap mode 등) 도 승계.
        /// </summary>
        private static AnimationClip BuildFilteredClip(AnimationClip source, HashSet<string> allowedPaths, string assetPath, int stride)
        {
            var copy = new AnimationClip();
            copy.name = source.name;
            copy.frameRate = source.frameRate;
            copy.wrapMode = source.wrapMode;
            copy.legacy = source.legacy;
            copy.localBounds = source.localBounds;
            AnimationUtility.SetAnimationClipSettings(copy, AnimationUtility.GetAnimationClipSettings(source));

            if (File.Exists(assetPath)) AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(copy, assetPath);

            foreach (var b in AnimationUtility.GetCurveBindings(source))
            {
                if (!allowedPaths.Contains(b.path ?? "")) continue;
                var curve = AnimationUtility.GetEditorCurve(source, b);
                if (curve == null) continue;
                if (stride > 1) curve = DecimateCurve(curve, stride);
                AnimationUtility.SetEditorCurve(copy, b, curve);
            }
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(source))
            {
                if (!allowedPaths.Contains(b.path ?? "")) continue;
                var keys = AnimationUtility.GetObjectReferenceCurve(source, b);
                if (keys != null) AnimationUtility.SetObjectReferenceCurve(copy, b, keys);
            }

            EditorUtility.SetDirty(copy);
            AssetDatabase.SaveAssets();
            return copy;
        }

        // ---------------------------------------------------------------------
        // Shell construction — 클립의 바인딩만을 근거로 최소 하이어라키 생성
        // ---------------------------------------------------------------------

        private static GameObject BuildShell(AnimationClip clip, List<Mesh> outCreatedMeshes)
        {
            var root = new GameObject(ShellRootName);
            root.hideFlags = HideFlags.DontSave;

            var floatBindings = AnimationUtility.GetCurveBindings(clip);
            var objBindings   = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            var all = floatBindings.Concat(objBindings).ToArray();

            // path -> (componentType -> needed blendshape names)
            // blendshape 이름 수집은 SkinnedMeshRenderer 전용
            var pathToComponents = new Dictionary<string, Dictionary<Type, HashSet<string>>>(StringComparer.Ordinal);

            foreach (var b in all)
            {
                if (!pathToComponents.TryGetValue(b.path, out var compMap))
                {
                    compMap = new Dictionary<Type, HashSet<string>>();
                    pathToComponents[b.path] = compMap;
                }
                if (b.type == null) continue; // 안전장치
                if (!compMap.TryGetValue(b.type, out var shapes))
                {
                    shapes = new HashSet<string>(StringComparer.Ordinal);
                    compMap[b.type] = shapes;
                }
                if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName != null &&
                    b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    shapes.Add(b.propertyName.Substring("blendShape.".Length));
                }
            }

            // 루트 자체 바인딩(path="") 은 root 에 반영, 그 외 경로는 하위 생성
            foreach (var kv in pathToComponents)
            {
                var path = kv.Key;
                Transform t = string.IsNullOrEmpty(path) ? root.transform : EnsurePath(root.transform, path);

                foreach (var compKv in kv.Value)
                {
                    var type = compKv.Key;
                    if (type == typeof(Transform) || type == typeof(GameObject))
                        continue; // Transform 은 경로 생성만으로 충족

                    // 컴포넌트가 없으면 추가 시도
                    if (t.GetComponent(type) != null) continue;
                    Component comp = null;
                    try { comp = t.gameObject.AddComponent(type); }
                    catch (Exception)
                    {
                        // 추가 불가능 타입(추상/스크립트 없음 등) — transform 바인딩만 유지되어도
                        // 대부분의 페이셜 클립은 SMR + Transform 조합이라 실무상 문제없음.
                        continue;
                    }

                    // SkinnedMeshRenderer 는 blendshape 이름을 갖는 메쉬를 붙여야 blendShape.* 커브가 보존됨
                    if (comp is SkinnedMeshRenderer smr)
                    {
                        var shapes = compKv.Value;
                        var mesh = BuildDummyBlendShapeMesh(shapes);
                        outCreatedMeshes.Add(mesh);
                        smr.sharedMesh = mesh;
                        smr.rootBone = t; // 참조 안정화
                    }
                }
            }

            return root;
        }

        /// <summary>
        /// path ("A/B/C") 를 따라 Transform 계층을 보장 — 없는 노드는 생성.
        /// </summary>
        private static Transform EnsurePath(Transform root, string path)
        {
            var parts = path.Split('/');
            var cur = root;
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var child = cur.Find(p);
                if (child == null)
                {
                    var go = new GameObject(p);
                    go.transform.SetParent(cur, false);
                    child = go.transform;
                }
                cur = child;
            }
            return cur;
        }

        /// <summary>
        /// 주어진 이름들을 blendshape 로 갖는 최소 메쉬 생성(1-vertex + 이름별 zero-delta 프레임).
        /// FBX Exporter 가 SMR.blendShape.* 커브를 이름 기반으로 내보낼 수 있게 해 준다.
        /// </summary>
        private static Mesh BuildDummyBlendShapeMesh(HashSet<string> shapeNames)
        {
            var mesh = new Mesh { name = "FacialBake_Dummy" };
            mesh.hideFlags = HideFlags.DontSave;
            mesh.vertices  = new[] { Vector3.zero };
            mesh.normals   = new[] { Vector3.up };
            mesh.triangles = new int[0];

            var zeroV = new[] { Vector3.zero };
            var zeroN = new[] { Vector3.zero };
            var zeroT = new[] { Vector3.zero };

            if (shapeNames != null)
            {
                foreach (var name in shapeNames)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    // 같은 이름의 블렌드셰이프는 중복 추가 불가 → try
                    try { mesh.AddBlendShapeFrame(name, 100f, zeroV, zeroN, zeroT); }
                    catch { /* 무시 */ }
                }
            }
            mesh.UploadMeshData(false);
            return mesh;
        }

        private static int CountTransforms(GameObject go)
            => go.GetComponentsInChildren<Transform>(true).Length;

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Unity FBX Exporter 의 ExportObject 를 리플렉션으로 래핑.
        /// 2-인자 오버로드는 모델만 내보내므로, IExportOptions 를 구성해 애니메이션 포함 옵션을 지정한다.
        /// </summary>
        private class FbxExportInvoker
        {
            private MethodInfo exportWithOptions; // (string, Object, IExportOptions)
            private MethodInfo exportBasic;       // (string, Object)  — 최후 폴백
            private Type optionsType;
            // 이 버전의 ExportModelOptions 는 setter 메서드가 아닌 프로퍼티를 가짐
            private PropertyInfo propInclude;            // ModelAnimIncludeOption
            private PropertyInfo propAnimSource;         // AnimationSource
            private PropertyInfo propAnimDest;           // AnimationDest
            private PropertyInfo propAnimateSkinnedMesh; // AnimateSkinnedMesh — 블렌드셰이프 필수
            private PropertyInfo propExportUnrendered;   // ExportUnrendered
            private PropertyInfo propEmbedTextures;      // EmbedTextures
            private PropertyInfo propExportFormat;       // ExportFormat (ASCII/Binary)
            private object includeModelAndAnim;
            private object exportFormatBinary;

            public static FbxExportInvoker Resolve()
            {
                Assembly asm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == FbxExporterAssemblyName) { asm = a; break; }
                }
                if (asm == null) return null;

                var exporterType = asm.GetType(FbxExporterTypeName);
                if (exporterType == null) return null;

                var inv = new FbxExportInvoker();

                // 3-인자 오버로드 탐색 — 파라미터 타입은 묻지 않고 시그니처로만 매칭
                foreach (var m in exporterType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ExportObject") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 3 &&
                        ps[0].ParameterType == typeof(string) &&
                        ps[1].ParameterType == typeof(UnityEngine.Object))
                    {
                        inv.exportWithOptions = m;
                        // 실제 요구되는 옵션 타입을 메서드에서 직접 취함
                        inv.optionsType = ps[2].ParameterType;
                        break;
                    }
                }

                // 2-인자 기본 오버로드
                inv.exportBasic = exporterType.GetMethod(
                    "ExportObject",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(UnityEngine.Object) },
                    null);

                if (inv.exportWithOptions == null && inv.exportBasic == null)
                    return null;

                // 옵션 인스턴스 생성 타입 결정:
                //   - optionsType 이 추상/인터페이스면 해당 어셈블리에서 구체 구현을 찾음
                Type concreteOptionsType = inv.optionsType;
                if (concreteOptionsType != null && (concreteOptionsType.IsAbstract || concreteOptionsType.IsInterface))
                {
                    concreteOptionsType = FindConcreteImplementation(asm, inv.optionsType);
                }

                if (concreteOptionsType != null)
                {
                    inv.optionsType              = concreteOptionsType;
                    inv.propInclude              = FindProp(concreteOptionsType, "ModelAnimIncludeOption");
                    inv.propAnimSource           = FindProp(concreteOptionsType, "AnimationSource");
                    inv.propAnimDest             = FindProp(concreteOptionsType, "AnimationDest");
                    inv.propAnimateSkinnedMesh   = FindProp(concreteOptionsType, "AnimateSkinnedMesh");
                    inv.propExportUnrendered     = FindProp(concreteOptionsType, "ExportUnrendered");
                    inv.propEmbedTextures        = FindProp(concreteOptionsType, "EmbedTextures");
                    inv.propExportFormat         = FindProp(concreteOptionsType, "ExportFormat");

                    // ExportFormat 열거형에서 Binary 값 추출
                    if (inv.propExportFormat != null && inv.propExportFormat.PropertyType.IsEnum)
                    {
                        try { inv.exportFormatBinary = Enum.Parse(inv.propExportFormat.PropertyType, "Binary"); }
                        catch { inv.exportFormatBinary = null; }
                    }

                    // Include 열거형 — 프로퍼티 타입에서 직접 추출 가능하면 우선
                    Type includeType = inv.propInclude?.PropertyType;
                    if (includeType == null || !includeType.IsEnum)
                    {
                        includeType =
                            asm.GetType("UnityEditor.Formats.Fbx.Exporter.ExportSettings+Include")
                            ?? asm.GetType("UnityEditor.Formats.Fbx.Exporter.Include")
                            ?? FindNestedEnum(asm, "Include");
                    }
                    if (includeType != null && includeType.IsEnum)
                    {
                        try { inv.includeModelAndAnim = Enum.Parse(includeType, "ModelAndAnim"); }
                        catch { inv.includeModelAndAnim = null; }
                    }
                }

                return inv;
            }

            private static PropertyInfo FindProp(Type t, string name)
            {
                return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? t.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }

            private static Type FindConcreteImplementation(Assembly asm, Type baseOrInterface)
            {
                Type best = null;
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!baseOrInterface.IsAssignableFrom(t)) continue;
                    // 기본 생성자 필요
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                    // 이름에 "Serialize" 가 포함된 쪽을 우선
                    if (t.Name.IndexOf("Serialize", StringComparison.OrdinalIgnoreCase) >= 0) return t;
                    if (best == null) best = t;
                }
                return best;
            }

            private static MethodInfo FindMethod(Type t, string name)
            {
                // 공개 메서드 우선, 없으면 비공개까지 (버전에 따라 internal 이기도 함)
                return t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
                    ?? t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }

            private static Type FindNestedEnum(Assembly asm, string simpleName)
            {
                foreach (var t in asm.GetTypes())
                {
                    foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        if (n.IsEnum && n.Name == simpleName) return n;
                }
                return null;
            }

            public string Describe()
            {
                var mode = exportWithOptions != null && optionsType != null && includeModelAndAnim != null
                    ? "options(ModelAndAnim)" : "basic(model only)";
                var parts = new List<string>
                {
                    $"mode={mode}",
                    $"optionsType={(optionsType != null ? optionsType.FullName : "<none>")}",
                    $"includeEnum={(includeModelAndAnim != null ? includeModelAndAnim.ToString() : "<none>")}",
                    $"Include={(propInclude != null ? "ok" : "MISSING")}",
                    $"AnimSrc={(propAnimSource != null ? "ok" : "MISSING")}",
                    $"AnimDest={(propAnimDest != null ? "ok" : "MISSING")}",
                    $"AnimateSkinnedMesh={(propAnimateSkinnedMesh != null ? "ok" : "MISSING")}",
                    $"Format={(exportFormatBinary != null ? "Binary" : "default")}",
                };
                return string.Join(", ", parts);
            }

            /// <summary>options 인스턴스의 공개/비공개 Setter 후보를 모두 나열 — 진단용.</summary>
            public string DumpOptionsMembers()
            {
                if (optionsType == null) return "(no optionsType)";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Members of {optionsType.FullName}:");
                foreach (var m in optionsType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.IsSpecialName) continue; // property getters/setters
                    var ps = m.GetParameters();
                    var sig = string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name));
                    sb.AppendLine($"  {(m.IsPublic ? "pub" : "int")} {m.ReturnType.Name} {m.Name}({sig})");
                }
                foreach (var p in optionsType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    sb.AppendLine($"  prop {p.PropertyType.Name} {p.Name} {{ {(p.CanRead ? "get;" : "")} {(p.CanWrite ? "set;" : "")} }}");
                }
                foreach (var f in optionsType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    sb.AppendLine($"  field {f.FieldType.Name} {f.Name}");
                }
                return sb.ToString();
            }

            public void Export(string path, GameObject root, Transform animRoot)
            {
                if (exportWithOptions != null && optionsType != null && includeModelAndAnim != null)
                {
                    var options = Activator.CreateInstance(optionsType);
                    // 핵심 설정:
                    //   ModelAnimIncludeOption = ModelAndAnim  → 애니메이션 포함
                    //   AnimateSkinnedMesh     = true          → SMR 블렌드셰이프 커브 포함 (페이셜 필수)
                    //   ExportUnrendered       = true          → 셸 SMR 에 머터리얼 없이도 유지
                    //   AnimationSource/Dest   = shell root    → 애니메이터 추적 기준
                    propInclude?           .SetValue(options, includeModelAndAnim);
                    propAnimateSkinnedMesh?.SetValue(options, true);
                    propExportUnrendered?  .SetValue(options, true);
                    propEmbedTextures?     .SetValue(options, false);
                    propAnimSource?        .SetValue(options, animRoot);
                    propAnimDest?          .SetValue(options, animRoot);
                    // ExportFormat = Binary — ASCII 에 비해 파일 크기가 대략 5~10× 작음
                    if (exportFormatBinary != null)
                        propExportFormat?.SetValue(options, exportFormatBinary);
                    exportWithOptions.Invoke(null, new object[] { path, (UnityEngine.Object)root, options });
                    return;
                }

                if (exportBasic != null)
                {
                    exportBasic.Invoke(null, new object[] { path, (UnityEngine.Object)root });
                    return;
                }

                throw new InvalidOperationException("사용 가능한 ExportObject 오버로드를 찾지 못했습니다.");
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private void Log(string line)
        {
            logLines.Add(line);
            Repaint();
        }
    }
}
