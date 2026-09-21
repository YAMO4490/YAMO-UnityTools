using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>루트 배율을 이름에 붙여 원본 프리팹 옆에서 기존 풀 파이프라인을 실행한다.</summary>
    public static class AvatarScaleBakeUtility
    {
        public static bool TryGetOutputPaths(GameObject source, out string fbxPath,
            out string prefabPath, out string error)
        {
            fbxPath = prefabPath = null;
            error = null;
            if (source == null)
            {
                error = "배율을 조절한 아바타 프리팹의 루트를 지정하세요.";
                return false;
            }

            // 씬 오브젝트 이름은 바뀔 수 있으므로 실제 연결된 프리팹의 경로/이름을 사용.
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(source))
            {
                error = "씬의 프리팹 인스턴스 루트를 지정하세요. 프리팹 연결이 없거나 자식 오브젝트이면 저장 위치를 결정할 수 없습니다.";
                return false;
            }
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
            if (string.IsNullOrEmpty(sourcePath) || !sourcePath.StartsWith("Assets/", System.StringComparison.Ordinal)
                || !sourcePath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                error = "Assets 폴더에 저장된 .prefab의 씬 인스턴스를 지정하세요.";
                return false;
            }

            var scale = source.transform.localScale;
            if (!IsPositiveFinite(scale.x) || !IsPositiveFinite(scale.y) || !IsPositiveFinite(scale.z)
                || !Mathf.Approximately(scale.x, scale.y) || !Mathf.Approximately(scale.x, scale.z))
            {
                error = "키 조절에는 X/Y/Z가 같은 양수 배율이 필요합니다. 예: (0.9, 0.9, 0.9).";
                return false;
            }

            var folder = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
            // 문화권에 관계없이 소수점은 '.', 0.9f는 '0.9'로 표시하며 정밀도를 유지.
            var name = Path.GetFileNameWithoutExtension(sourcePath)
                + "_scale_" + scale.x.ToString("R", CultureInfo.InvariantCulture);
            fbxPath = folder + "/" + name + ".fbx";
            prefabPath = folder + "/" + name + ".prefab";
            return true;
        }

        public static bool Run(AvatarBakeOptions options)
        {
            if (options == null) throw new System.ArgumentNullException(nameof(options));
            if (!TryGetOutputPaths(options.Source, out var fbxPath, out var prefabPath, out var error))
            {
                (options.Log ?? new DebugMigrationLog("[ScaleBake] ")).Error(error);
                return false;
            }

            options.FbxProjectPath = fbxPath;
            options.PrefabProjectPath = prefabPath;
            // 같은 폴더에 있는 원본/다른 배율의 명세를 덮어쓰지 않는다.
            var previousDocumentName = options.HiddenObjectsDocumentName;
            options.HiddenObjectsDocumentName = Path.GetFileNameWithoutExtension(prefabPath) + "_HiddenObjects.md";
            var source = options.Source;
            GameObject workingCopy = null;
            try
            {
                workingCopy = Object.Instantiate(source);
                workingCopy.name = source.name;
                // 부모의 배율·씬 배치가 결과 크기에 섞이지 않게 루트의 localScale만 적용.
                workingCopy.transform.SetParent(null, false);
                workingCopy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                workingCopy.transform.localScale = source.transform.localScale;
                options.Source = workingCopy;
                return AvatarBakePipeline.Run(options);
            }
            finally
            {
                options.Source = source;
                options.HiddenObjectsDocumentName = previousDocumentName;
                if (workingCopy != null) Object.DestroyImmediate(workingCopy);
            }
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
