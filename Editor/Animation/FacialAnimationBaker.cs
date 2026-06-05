using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Facial Animation Baker
    ///
    /// .anim 페이셜 애니메이션(블렌드셰이프가 많아 용량이 큰 YAML 클립)을
    /// 커브 최적화를 통해 .anim 형식 그대로 용량을 줄이는 에디터 도구.
    ///
    /// 최적화 기법:
    ///   - RDP(Ramer-Douglas-Peucker) 기반 키프레임 감소: 오차 허용 범위 내에서
    ///     불필요한 중간 키프레임을 제거.
    ///   - 상수 커브 제거: 값이 변하지 않는 커브를 제거하거나 최소화.
    ///   - 제로 커브 제거: 모든 값이 0인 커브를 완전히 제거.
    ///   - 정밀도 축소: float 값의 소수점 자릿수를 줄여 YAML 텍스트 크기 감소.
    ///
    /// 페이셜 클립은 여러 캐릭터가 돌려 쓰므로 "원본 캐릭터"에 의존하지 않는다.
    /// 바인딩(path / propertyName / 블렌드셰이프 이름)을 그대로 보존하므로,
    /// 결과 .anim 은 기존과 동일하게 어떤 캐릭터에도 적용할 수 있다.
    /// </summary>
    public class FacialAnimationBaker : EditorWindow
    {
        private static FacialAnimationBaker _instance;

        private readonly List<AnimationClip> clips = new List<AnimationClip>();
        private string outputFolderPath = DefaultOutputFolder;
        private bool overwriteExisting = true;
        private bool pingAfterExport = true;

        // 최적화 옵션
        private float errorTolerance = 0.5f;        // RDP 오차 허용치 (블렌드셰이프 0~100 기준)
        private bool removeConstantCurves = true;    // 값이 변하지 않는 커브 제거
        private bool removeZeroCurves = true;        // 모든 값이 0인 커브 제거
        private int precisionDigits = 4;             // float 소수점 자릿수

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

        [MenuItem("Tools/YAMO/Animation/Facial Animation Baker")]
        [Shortcut("YAMO/Facial Animation Baker", KeyCode.Alpha7, ShortcutModifiers.None)]
        public static void ShowWindow()
        {
            if (_instance != null) { _instance.Close(); return; }
            var w = GetWindow<FacialAnimationBaker>("Facial Anim Baker");
            w.minSize = new Vector2(440, 460);
        }

        private void OnEnable()  => _instance = this;
        private void OnDisable() => _instance = null;

        // ---------------------------------------------------------------------
        // GUI
        // ---------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Facial Animation Optimizer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                ".anim 페이셜 클립의 커브를 최적화해 용량을 줄입니다.\n" +
                "바인딩(path / propertyName / 블렌드셰이프 이름)을 그대로 보존하므로,\n" +
                "결과 .anim 은 기존과 동일하게 어떤 캐릭터에도 적용할 수 있습니다.",
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

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Optimization", EditorStyles.boldLabel);

            // 오차 허용치
            using (new EditorGUILayout.HorizontalScope())
            {
                errorTolerance = EditorGUILayout.FloatField(
                    new GUIContent("Error Tolerance",
                        "RDP 키프레임 감소의 오차 허용치.\n" +
                        "블렌드셰이프(0~100)의 경우 0.5 = 0.5% 오차.\n" +
                        "값이 클수록 더 많은 키가 제거되어 용량이 줄지만 품질이 낮아집니다."),
                    errorTolerance);
                if (errorTolerance < 0f) errorTolerance = 0f;
                GUILayout.Label(errorTolerance == 0f ? "(키 감소 없음)" : $"(±{errorTolerance})",
                    EditorStyles.miniLabel, GUILayout.Width(100));
            }

            removeConstantCurves = EditorGUILayout.Toggle(
                new GUIContent("Remove Constant Curves",
                    "값이 변하지 않는 커브를 제거합니다.\n" +
                    "예: 특정 블렌드셰이프가 항상 0이면 해당 커브를 삭제."),
                removeConstantCurves);

            removeZeroCurves = EditorGUILayout.Toggle(
                new GUIContent("Remove Zero Curves",
                    "모든 키프레임 값이 0인 커브를 제거합니다."),
                removeZeroCurves);

            using (new EditorGUILayout.HorizontalScope())
            {
                precisionDigits = EditorGUILayout.IntField(
                    new GUIContent("Precision Digits",
                        "float 값의 소수점 자릿수.\n" +
                        "4 = 0.1234 (권장), 3 = 0.123 (더 공격적).\n" +
                        "0 = 정밀도 축소 없음."),
                    precisionDigits);
                if (precisionDigits < 0) precisionDigits = 0;
                if (precisionDigits > 10) precisionDigits = 10;
                GUILayout.Label(precisionDigits == 0 ? "(축소 없음)" : $"(소수점 {precisionDigits}자리)",
                    EditorStyles.miniLabel, GUILayout.Width(110));
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(!HasAnyClip()))
            {
                if (GUILayout.Button("Optimize", GUILayout.Height(28)))
                    OptimizeAll();
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
        // Optimize pipeline
        // ---------------------------------------------------------------------

        /// <summary>OptimizeOne 의 결과를 임시 보관해 StopAssetEditing 이후 크기 비교에 사용.</summary>
        private struct OptimizeResult
        {
            public string clipName;
            public string animPath;
            public string origPath;
            public int origCurveCount, newCurveCount;
            public int origKeyCount, newKeyCount;
            public int removedConstant, removedZero;
        }

        private void OptimizeAll()
        {
            logLines.Clear();

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
            var results = new List<OptimizeResult>();
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < clips.Count; i++)
                {
                    var clip = clips[i];
                    if (clip == null) continue;
                    EditorUtility.DisplayProgressBar("Facial Anim Optimize", clip.name, (float)i / Mathf.Max(1, clips.Count));
                    if (OptimizeOne(clip, folderPath, out var result))
                    {
                        results.Add(result);
                        ok++;
                    }
                    else fail++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            // StopAssetEditing + Refresh 이후 파일이 디스크에 플러시되었으므로 크기 비교 가능
            foreach (var r in results)
            {
                long origSize = GetFileSize(r.origPath);
                long newSize = GetFileSize(r.animPath);
                float ratio = origSize > 0 ? (float)newSize / origSize * 100f : 0f;

                Log($"✓ [{r.clipName}] 커브 {r.origCurveCount}→{r.newCurveCount} " +
                    $"(상수 -{r.removedConstant}, 제로 -{r.removedZero}) / " +
                    $"키 {r.origKeyCount}→{r.newKeyCount} / " +
                    $"크기 {FormatBytes(origSize)}→{FormatBytes(newSize)} ({ratio:F1}%)");

                if (pingAfterExport)
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(r.animPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
            }

            Log($"완료 — 성공 {ok} / 실패 {fail}");
        }

        private bool OptimizeOne(AnimationClip clip, string folderPath, out OptimizeResult result)
        {
            result = default;
            string fileName = SanitizeFileName(clip.name) + ".anim";
            string animPath = Path.Combine(folderPath, fileName).Replace('\\', '/');
            if (!overwriteExisting && File.Exists(animPath))
            {
                Log($"- 건너뜀(이미 존재): {animPath}");
                return false;
            }

            try
            {
                // 선택된 path 목록
                var sel = GetOrScanSelection(clip);
                var allowed = new HashSet<string>(
                    sel.include.Where(kv => kv.Value).Select(kv => kv.Key),
                    StringComparer.Ordinal);
                if (allowed.Count == 0)
                {
                    Log($"✗ [{clip.name}] 선택된 오브젝트가 없어 건너뜀");
                    return false;
                }

                // 원본 통계
                var origBindings = AnimationUtility.GetCurveBindings(clip);
                var origObjBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                int origCurveCount = origBindings.Length + origObjBindings.Length;
                int origKeyCount = 0;
                foreach (var b in origBindings)
                {
                    var c = AnimationUtility.GetEditorCurve(clip, b);
                    if (c != null) origKeyCount += c.keys.Length;
                }

                // 최적화된 클립 생성
                var optimized = new AnimationClip();
                optimized.name = clip.name;
                optimized.frameRate = clip.frameRate;
                optimized.wrapMode = clip.wrapMode;
                optimized.legacy = clip.legacy;
                optimized.localBounds = clip.localBounds;
                AnimationUtility.SetAnimationClipSettings(optimized, AnimationUtility.GetAnimationClipSettings(clip));

                // 기존 에셋이 있으면 삭제
                if (File.Exists(animPath)) AssetDatabase.DeleteAsset(animPath);
                AssetDatabase.CreateAsset(optimized, animPath);

                int newCurveCount = 0;
                int newKeyCount = 0;
                int removedConstant = 0;
                int removedZero = 0;

                // float 커브 최적화
                foreach (var b in origBindings)
                {
                    if (!allowed.Contains(b.path ?? "")) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    if (curve == null) continue;

                    // 제로 커브 제거
                    if (removeZeroCurves && IsZeroCurve(curve, errorTolerance))
                    {
                        removedZero++;
                        continue;
                    }

                    // 상수 커브 제거
                    if (removeConstantCurves && IsConstantCurve(curve, errorTolerance))
                    {
                        removedConstant++;
                        continue;
                    }

                    // RDP 키프레임 감소
                    if (errorTolerance > 0f)
                        curve = ReduceCurveRDP(curve, errorTolerance);

                    // 모든 키프레임을 Linear 보간으로 설정
                    curve = LinearizeCurve(curve);

                    // 정밀도 축소
                    if (precisionDigits > 0)
                        curve = ReduceCurvePrecision(curve, precisionDigits);

                    AnimationUtility.SetEditorCurve(optimized, b, curve);
                    newCurveCount++;
                    newKeyCount += curve.keys.Length;
                }

                // ObjectReference 커브 복사 (최적화 대상 아님)
                foreach (var b in origObjBindings)
                {
                    if (!allowed.Contains(b.path ?? "")) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                    if (keys != null)
                    {
                        AnimationUtility.SetObjectReferenceCurve(optimized, b, keys);
                        newCurveCount++;
                    }
                }

                EditorUtility.SetDirty(optimized);

                result = new OptimizeResult
                {
                    clipName = clip.name,
                    animPath = animPath,
                    origPath = AssetDatabase.GetAssetPath(clip),
                    origCurveCount = origCurveCount,
                    newCurveCount = newCurveCount,
                    origKeyCount = origKeyCount,
                    newKeyCount = newKeyCount,
                    removedConstant = removedConstant,
                    removedZero = removedZero,
                };
                return true;
            }
            catch (Exception e)
            {
                Log($"✗ [{clip.name}] 실패: {e.GetBaseException().Message}");
                Debug.LogException(e);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // Curve optimization algorithms
        // ---------------------------------------------------------------------

        /// <summary>
        /// Ramer-Douglas-Peucker 알고리즘으로 오차 허용 범위 내에서 키프레임을 감소.
        /// 각 키프레임의 time/value 를 2D 점으로 취급하고, 양 끝을 잇는 선분으로부터
        /// 최대 오차를 초과하는 키만 유지.
        /// </summary>
        private static AnimationCurve ReduceCurveRDP(AnimationCurve src, float tolerance)
        {
            var keys = src.keys;
            if (keys.Length <= 2 || tolerance <= 0f) return src;

            var keep = new bool[keys.Length];
            keep[0] = true;
            keep[keys.Length - 1] = true;

            RDPRecurse(keys, keep, 0, keys.Length - 1, tolerance);

            var kept = new List<Keyframe>(keys.Length);
            for (int i = 0; i < keys.Length; i++)
                if (keep[i]) kept.Add(keys[i]);

            var result = new AnimationCurve(kept.ToArray());
            result.preWrapMode  = src.preWrapMode;
            result.postWrapMode = src.postWrapMode;
            return result;
        }

        private static void RDPRecurse(Keyframe[] keys, bool[] keep, int start, int end, float tolerance)
        {
            if (end - start <= 1) return;

            float t0 = keys[start].time;
            float t1 = keys[end].time;
            float v0 = keys[start].value;
            float v1 = keys[end].value;
            float dt = t1 - t0;

            float maxError = 0f;
            int maxIdx = -1;

            for (int i = start + 1; i < end; i++)
            {
                // 선형 보간으로 예상되는 값과 실제 값의 차이
                float t = dt > 0f ? (keys[i].time - t0) / dt : 0f;
                float interpolated = v0 + (v1 - v0) * t;
                float error = Mathf.Abs(keys[i].value - interpolated);
                if (error > maxError)
                {
                    maxError = error;
                    maxIdx = i;
                }
            }

            if (maxError > tolerance && maxIdx >= 0)
            {
                keep[maxIdx] = true;
                RDPRecurse(keys, keep, start, maxIdx, tolerance);
                RDPRecurse(keys, keep, maxIdx, end, tolerance);
            }
        }

        /// <summary>
        /// 모든 키프레임의 탄젠트를 Linear 로 재계산.
        /// Bezier/Hermite 탄젠트가 남아 있으면 키프레임 제거 후 오버슈트가 발생하므로,
        /// 인접 키 사이의 기울기를 직접 계산해 설정한다.
        /// </summary>
        private static AnimationCurve LinearizeCurve(AnimationCurve src)
        {
            var keys = src.keys;
            if (keys.Length == 0) return src;

            for (int i = 0; i < keys.Length; i++)
            {
                float inTangent = 0f;
                float outTangent = 0f;

                if (i > 0)
                {
                    float dt = keys[i].time - keys[i - 1].time;
                    inTangent = dt > 0f ? (keys[i].value - keys[i - 1].value) / dt : 0f;
                }

                if (i < keys.Length - 1)
                {
                    float dt = keys[i + 1].time - keys[i].time;
                    outTangent = dt > 0f ? (keys[i + 1].value - keys[i].value) / dt : 0f;
                }

                keys[i].inTangent = inTangent;
                keys[i].outTangent = outTangent;
                keys[i].inWeight = 0f;
                keys[i].outWeight = 0f;
            }

            var result = new AnimationCurve(keys);
            result.preWrapMode  = src.preWrapMode;
            result.postWrapMode = src.postWrapMode;

            // 탄젠트 모드를 Linear 로 명시 설정
            for (int i = 0; i < result.keys.Length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(result, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(result, i, AnimationUtility.TangentMode.Linear);
            }

            return result;
        }

        /// <summary>
        /// 커브의 모든 키프레임 값이 동일한지 확인 (허용 오차 이내).
        /// </summary>
        private static bool IsConstantCurve(AnimationCurve curve, float tolerance)
        {
            var keys = curve.keys;
            if (keys.Length <= 1) return true;
            float first = keys[0].value;
            for (int i = 1; i < keys.Length; i++)
                if (Mathf.Abs(keys[i].value - first) > tolerance) return false;
            return true;
        }

        /// <summary>
        /// 커브의 모든 키프레임 값이 0인지 확인 (허용 오차 이내).
        /// </summary>
        private static bool IsZeroCurve(AnimationCurve curve, float tolerance)
        {
            var keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
                if (Mathf.Abs(keys[i].value) > tolerance) return false;
            return true;
        }

        /// <summary>
        /// 키프레임의 value, inTangent, outTangent, inWeight, outWeight 를
        /// 지정된 소수점 자릿수로 반올림.
        /// </summary>
        private static AnimationCurve ReduceCurvePrecision(AnimationCurve src, int digits)
        {
            var keys = src.keys;
            bool changed = false;
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                var newValue     = (float)Math.Round(k.value, digits);
                var newInTangent  = (float)Math.Round(k.inTangent, digits);
                var newOutTangent = (float)Math.Round(k.outTangent, digits);
                var newInWeight   = (float)Math.Round(k.inWeight, digits);
                var newOutWeight  = (float)Math.Round(k.outWeight, digits);

                if (newValue != k.value || newInTangent != k.inTangent ||
                    newOutTangent != k.outTangent || newInWeight != k.inWeight ||
                    newOutWeight != k.outWeight)
                {
                    k.value      = newValue;
                    k.inTangent  = newInTangent;
                    k.outTangent = newOutTangent;
                    k.inWeight   = newInWeight;
                    k.outWeight  = newOutWeight;
                    keys[i] = k;
                    changed = true;
                }
            }

            if (!changed) return src;

            var result = new AnimationCurve(keys);
            result.preWrapMode  = src.preWrapMode;
            result.postWrapMode = src.postWrapMode;
            return result;
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static long GetFileSize(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return 0;
            var fullPath = Path.GetFullPath(assetPath);
            return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F2} MB";
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
