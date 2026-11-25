using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YAMO.Editor
{
    /// <summary>Extracts ARKit curves only. Animator root must have a direct child named Face.</summary>
    public static class FacialFbxToAnimation
    {
        public static string GetAssetSourcePath(string source)
        {
            string full = Path.GetFullPath(source).Replace('\\', '/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/') + "/";
            return full.StartsWith(assets, StringComparison.OrdinalIgnoreCase)
                ? "Assets/" + full.Substring(assets.Length) : null;
        }

        public static string GetOutputPath(AnimationClip source)
        {
            string assetPath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(assetPath)) throw new ArgumentException("저장된 원본 클립이 필요합니다.");
            string folder = assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                ? Path.GetDirectoryName(assetPath).Replace('\\', '/') : EnsureRecordingsFolder();
            return folder + "/" + Path.GetFileNameWithoutExtension(assetPath) + "_ARKit_Face.anim";
        }

        public static string EnsureRecordingsFolder()
        {
            const string folder = "Assets/Recordings";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets", "Recordings");
            return folder;
        }

        public static readonly string[] ShapeNames = (
            "browDownLeft browDownRight browInnerUp browOuterUpLeft browOuterUpRight " +
            "cheekPuff cheekSquintLeft cheekSquintRight eyeBlinkLeft eyeBlinkRight " +
            "eyeLookDownLeft eyeLookDownRight eyeLookInLeft eyeLookInRight eyeLookOutLeft eyeLookOutRight " +
            "eyeLookUpLeft eyeLookUpRight eyeSquintLeft eyeSquintRight eyeWideLeft eyeWideRight " +
            "jawForward jawLeft jawOpen jawRight mouthClose mouthDimpleLeft mouthDimpleRight " +
            "mouthFrownLeft mouthFrownRight mouthFunnel mouthLeft mouthLowerDownLeft mouthLowerDownRight " +
            "mouthPressLeft mouthPressRight mouthPucker mouthRight mouthRollLower mouthRollUpper " +
            "mouthShrugLower mouthShrugUpper mouthSmileLeft mouthSmileRight mouthStretchLeft mouthStretchRight " +
            "mouthUpperUpLeft mouthUpperUpRight noseSneerLeft noseSneerRight tongueOut").Split(' ');

        private static string Normalize(string name)
        {
            // Exporters may use Face.eyeBlinkLeft, namespace:eyeBlinkLeft or eyeBlink_L.
            name = name.Substring(Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':')) + 1);
            if (name.EndsWith("_L", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 2) + "Left";
            if (name.EndsWith("_R", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 2) + "Right";
            return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static string Canonical(string property)
        {
            const string prefix = "blendShape.";
            if (!property.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string normalized = Normalize(property.Substring(prefix.Length));
            return ShapeNames.FirstOrDefault(n => Normalize(n) == normalized);
        }

        public static string[] GetSourcePaths(AnimationClip source)
        {
            if (!source) return Array.Empty<string>();
            return AnimationUtility.GetCurveBindings(source)
                .Where(b => b.type == typeof(SkinnedMeshRenderer) && Canonical(b.propertyName) != null)
                .Select(b => b.path).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        public static AnimationClip Extract(AnimationClip source, string sourcePath, bool fillMissing, out string[] missing)
        {
            if (!source) throw new ArgumentException("원본 클립을 선택하세요.");
            var channels = new Dictionary<string, AnimationCurve>(StringComparer.Ordinal);
            foreach (var binding in AnimationUtility.GetCurveBindings(source))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || binding.path != sourcePath) continue;
                string shape = Canonical(binding.propertyName);
                if (shape == null) continue;
                if (channels.ContainsKey(shape)) throw new InvalidDataException("동일 ARKit 키로 변환되는 채널이 중복됩니다: " + shape);
                var curve = AnimationUtility.GetEditorCurve(source, binding);
                if (curve != null && curve.length > 0) channels.Add(shape, curve);
            }
            if (channels.Count == 0) throw new InvalidDataException("선택한 메시 경로에 ARKit 애니메이션 커브가 없습니다.");
            missing = ShapeNames.Where(n => !channels.ContainsKey(n)).ToArray();
            var result = new AnimationClip { name = source.name + "_ARKit_Face", frameRate = source.frameRate };
            try
            {
                foreach (string shape in ShapeNames)
                {
                    if (!channels.TryGetValue(shape, out var curve))
                    {
                        if (!fillMissing) continue;
                        curve = AnimationCurve.Constant(0, source.length, 0);
                    }
                    AnimationUtility.SetEditorCurve(result,
                        EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape." + shape), curve);
                }
                var settings = AnimationUtility.GetAnimationClipSettings(source);
                settings.loopTime = false;
                settings.loopBlend = false;
                AnimationUtility.SetAnimationClipSettings(result, settings);
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        public static string Save(AnimationClip source, string sourcePath, bool fillMissing, string output, out string[] missing)
        {
            output = output.Replace('\\', '/');
            if (!output.StartsWith("Assets/", StringComparison.Ordinal) || output.Split('/').Any(p => p == ".." || p == ".") ||
                !output.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsValidFolder(Path.GetDirectoryName(output).Replace('\\', '/')))
                throw new ArgumentException("Assets 아래의 유효한 .anim 저장 경로가 필요합니다.");
            var clip = Extract(source, sourcePath, fillMissing, out missing);
            try
            {
                output = AssetDatabase.GenerateUniqueAssetPath(output);
                AssetDatabase.CreateAsset(clip, output);
                AssetDatabase.SaveAssetIfDirty(clip);
                return output;
            }
            catch { if (!AssetDatabase.Contains(clip)) UnityEngine.Object.DestroyImmediate(clip); throw; }
        }
    }

    public sealed class FacialFbxToAnimationWindow : EditorWindow
    {
        [SerializeField] private UnityEngine.Object input;
        [SerializeField] private int clipIndex, pathIndex;
        [SerializeField] private bool fillMissing;
        private string report = "";
        private Vector2 scroll;

        [MenuItem("Tools/YAMO/Animation/Convert Facial FBX to Anim (ARKit 52)")]
        public static void Open() => GetWindow<FacialFbxToAnimationWindow>("FBX → ARKit Anim").Show();

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("ARKit 52종 중 녹화된 커브만 추출합니다. 출력은 Face / SkinnedMeshRenderer / blendShape.<ARKit 키>입니다. Animator 기준 바로 아래에 Face 메시가 있어야 합니다. 값 배율은 변경하지 않습니다.", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            input = EditorGUILayout.ObjectField("FBX 또는 AnimationClip", input, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck()) { clipIndex = pathIndex = 0; report = ""; }
            if (GUILayout.Button("FBX 파일 선택…")) ImportExternal();
            var clips = input is AnimationClip single ? new[] { single } : input
                ? AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(input)).OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).ToArray()
                : Array.Empty<AnimationClip>();
            if (clips.Length == 0) { EditorGUILayout.HelpBox("애니메이션이 포함된 FBX 또는 클립을 선택하세요.", MessageType.Warning); return; }
            clipIndex = Mathf.Clamp(clipIndex, 0, clips.Length - 1);
            EditorGUI.BeginChangeCheck();
            clipIndex = EditorGUILayout.Popup("녹화 클립", clipIndex, clips.Select(c => c.name).ToArray());
            if (EditorGUI.EndChangeCheck()) pathIndex = 0;
            var clip = clips[clipIndex];
            var paths = FacialFbxToAnimation.GetSourcePaths(clip);
            if (paths.Length == 0) { EditorGUILayout.HelpBox("ARKit 블렌드셰이프 커브가 없습니다. FBX Import Settings의 Import Animation / Import BlendShapes를 확인하세요.", MessageType.Warning); return; }
            pathIndex = Mathf.Clamp(pathIndex, 0, paths.Length - 1);
            pathIndex = EditorGUILayout.Popup("원본 메시 경로", pathIndex, paths.Select(p => p.Length == 0 ? "(Root)" : p).ToArray());
            fillMissing = EditorGUILayout.Toggle("없는 키를 0으로 채우기", fillMissing);
            EditorGUILayout.HelpBox("0 채우기를 켜면 누락된 표정도 0으로 제어하여 총 52개 커브를 만듭니다. 끄면 원본에 있는 키만 저장합니다. 출력 키 이름은 eyeBlinkLeft 등 표준 ARKit 표기입니다.", MessageType.None);
            EditorGUILayout.LabelField("자동 저장 위치", FacialFbxToAnimation.GetOutputPath(clip), EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("Face용 .anim 저장", GUILayout.Height(32)))
            {
                string output = FacialFbxToAnimation.GetOutputPath(clip);
                if (!string.IsNullOrEmpty(output))
                {
                    try
                    {
                        string saved = FacialFbxToAnimation.Save(clip, paths[pathIndex], fillMissing, output, out var missing);
                        report = saved + "\n원본 ARKit 키: " + (52 - missing.Length) + "/52\n누락: " + (missing.Length == 0 ? "없음" : string.Join(", ", missing));
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(saved));
                    }
                    catch (Exception ex) { report = ex.Message; Debug.LogException(ex); }
                }
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField(report, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        private void ImportExternal()
        {
            string source = EditorUtility.OpenFilePanel("녹화 FBX 선택", "", "fbx");
            if (string.IsNullOrEmpty(source)) return;
            try
            {
                string destination = FacialFbxToAnimation.GetAssetSourcePath(source);
                bool copied = destination == null;
                if (copied)
                {
                    destination = AssetDatabase.GenerateUniqueAssetPath(FacialFbxToAnimation.EnsureRecordingsFolder() + "/" + Path.GetFileName(source));
                    File.Copy(source, Path.GetFullPath(destination), false);
                }
                AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
                input = AssetDatabase.LoadMainAssetAtPath(destination);
                clipIndex = pathIndex = 0;
                report = (copied ? "FBX 복사: " : "FBX 선택: ") + destination;
            }
            catch (Exception ex) { report = ex.Message; Debug.LogException(ex); }
        }
    }
}
