using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
#if UNITY_6000_3_OR_NEWER
using UnityEditor.Toolbars;
#endif
using UnityEngine;

namespace YAMO.Editor
{
    public static class FacialCsvToAnimation
    {
        private const string ToolbarPath = "YAMO/CSV → Anim";

        #if UNITY_6000_3_OR_NEWER
        [MainToolbarElement(ToolbarPath)]
        public static MainToolbarElement CreateToolbarButton()
        {
            return new MainToolbarButton(new MainToolbarContent("CSV → Anim"), Open);
        }

        #endif

        [MenuItem("Tools/YAMO/Animation/Convert Facial CSV to Anim")]
        private static void Open()
        {
            FacialCsvToAnimationWindow.ShowWindow();
        }

        public static string ConvertAndSave(string input, string outputFolder)
        {
            if (string.IsNullOrEmpty(outputFolder) ||
                !(outputFolder == "Assets" || outputFolder.StartsWith("Assets/", StringComparison.Ordinal)) ||
                !AssetDatabase.IsValidFolder(outputFolder))
                throw new ArgumentException("Assets 아래의 출력 폴더를 선택하세요.");

            string path = AssetDatabase.GenerateUniqueAssetPath(
                outputFolder + "/" + Path.GetFileNameWithoutExtension(input) + ".anim");
            var clip = Convert(input);
            try
            {
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssetIfDirty(clip);
                return path;
            }
            catch
            {
                if (!AssetDatabase.Contains(clip)) UnityEngine.Object.DestroyImmediate(clip);
                throw;
            }
        }

        public static AnimationClip Convert(string input)
        {
            using var reader = new StreamReader(input);
            var header = reader.ReadLine()?.Split(',').Select(field => field.Trim()).ToArray();
            if (header == null || header.Length < 2 || header[0] != "time_s")
                throw new InvalidDataException("첫 열이 time_s인 페이셜 CSV를 선택하세요.");

            int[] facialColumns = Enumerable.Range(1, header.Length - 1)
                .Where(column => !IsHeadColumn(header[column])).ToArray();
            int[] headColumns = Enumerable.Range(1, header.Length - 1)
                .Where(column => IsHeadColumn(header[column])).ToArray();
            string[] shapes = facialColumns.Select(column => ShapeName(header[column])).ToArray();
            if (shapes.Length == 0 || shapes.Any(string.IsNullOrWhiteSpace) ||
                shapes.Distinct(StringComparer.Ordinal).Count() != shapes.Length)
                throw new InvalidDataException("표정 채널 이름이 비어 있거나 중복되어 있습니다.");

            var samples = new System.Collections.Generic.List<float[]>();
            int lineNumber = 1;
            double firstTime = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] fields = line.Split(',');
                if (fields.Length != header.Length)
                    throw new InvalidDataException($"{lineNumber}행의 열 개수가 헤더와 다릅니다.");
                if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time) ||
                    double.IsNaN(time) || double.IsInfinity(time))
                    throw new InvalidDataException($"{lineNumber}행의 시간이 올바르지 않습니다.");

                if (samples.Count == 0)
                    firstTime = time;
                var sample = new float[header.Length];
                sample[0] = (float)(time - firstTime);
                if (float.IsInfinity(sample[0]) || (samples.Count > 0 && sample[0] <= samples[^1][0]))
                    throw new InvalidDataException($"{lineNumber}행의 시간이 이전 샘플보다 뒤에 있어야 합니다.");

                for (int column = 1; column < fields.Length; column++)
                {
                    if (!float.TryParse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture, out sample[column]) ||
                        float.IsNaN(sample[column]) || float.IsInfinity(sample[column]))
                        throw new InvalidDataException($"{lineNumber}행의 {header[column]} 값이 숫자가 아닙니다.");
                }

                if (headColumns.Any(column => sample[column] != 0f))
                    throw new InvalidDataException($"{lineNumber}행에 머리 모션이 있습니다. 이 변환기는 표정 채널과 값이 0인 head0~head5 열을 지원합니다.");
                samples.Add(sample);
            }

            if (samples.Count == 0)
                throw new InvalidDataException("CSV에 변환할 샘플이 없습니다.");

            var bindings = new EditorCurveBinding[shapes.Length];
            var curves = new AnimationCurve[shapes.Length];
            for (int channel = 0; channel < shapes.Length; channel++)
            {
                int column = facialColumns[channel];
                var keys = samples.Select(sample => new Keyframe(sample[0], sample[column])).ToArray();
                for (int index = 1; index < keys.Length; index++)
                {
                    var previous = keys[index - 1];
                    var current = keys[index];
                    float slope = (current.value - previous.value) / (current.time - previous.time);
                    previous.outTangent = slope;
                    current.inTangent = slope;
                    keys[index - 1] = previous;
                    keys[index] = current;
                }

                bindings[channel] = EditorCurveBinding.FloatCurve(
                    "Face", typeof(SkinnedMeshRenderer), "blendShape." + shapes[channel]);
                curves[channel] = new AnimationCurve(keys);
            }

            var clip = new AnimationClip { frameRate = 60f, name = Path.GetFileNameWithoutExtension(input) };
            AnimationUtility.SetEditorCurves(clip, bindings, curves);
            return clip;
        }

        private static bool IsHeadColumn(string name)
        {
            return name.Length == 5 && name.StartsWith("head", StringComparison.Ordinal) &&
                name[4] >= '0' && name[4] <= '5';
        }

        private static string ShapeName(string channel)
        {
            if (channel.EndsWith("_L", StringComparison.Ordinal))
                return channel.Substring(0, channel.Length - 2) + "Left";
            if (channel.EndsWith("_R", StringComparison.Ordinal))
                return channel.Substring(0, channel.Length - 2) + "Right";
            return channel;
        }
    }
}
