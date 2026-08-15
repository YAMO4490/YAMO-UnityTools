using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_2021_2_OR_NEWER
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
#endif

namespace YAMO.UnityTools.Editor
{
    public static class MainCameraScreenshotCapture
    {
        private const string MenuPath = "Tools/YAMO/Camera/Capture Main Camera Screenshot";
        private const string ShortcutId = "YAMO/Capture Main Camera Screenshot";
        private const string OutputFolder = "Assets/Screenshots";
        private const int FallbackWidth = 1920;
        private const int FallbackHeight = 1080;
        private const string UniversalRenderPipelineAssetTypeName =
            "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset";

        [MenuItem(MenuPath)]
        public static void Capture()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                EditorUtility.DisplayDialog(
                    "YAMO Screenshot",
                    "MainCamera 태그가 지정된 카메라를 찾을 수 없습니다.",
                    "확인");
                return;
            }

            Vector2Int size = ResolveGameViewSize(camera);
            string outputPath = BuildOutputPath(camera);

            try
            {
                CaptureCamera(camera, size.x, size.y, outputPath);
                Debug.Log($"[YAMO Screenshot] Saved: {outputPath}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "YAMO Screenshot",
                    "스크린샷 저장 중 오류가 발생했습니다. Console 로그를 확인해주세요.",
                    "확인");
            }
        }

        [Shortcut(ShortcutId)]
        private static void CaptureViaShortcut()
        {
            Capture();
        }

        private static void CaptureCamera(Camera camera, int width, int height, string outputPath)
        {
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            object renderPipelineAsset = null;
            PropertyInfo renderScaleProperty = null;
            float previousRenderScale = 1f;
            bool renderScaleOverridden = false;
            RenderTexture renderTexture = null;
            RenderTexture outputTexture = null;
            Texture2D texture = null;

            try
            {
                float captureScale = 1f;
                if (TryGetUrpRenderScale(
                        out renderPipelineAsset,
                        out renderScaleProperty,
                        out previousRenderScale) &&
                    previousRenderScale > 1f)
                {
                    captureScale = previousRenderScale;

                    // URP 12 applies Render Scale's final scaling pass even when a Camera renders
                    // into an explicitly-sized targetTexture. That mixes the Game View coordinates
                    // with the offscreen target coordinates and can shift the captured image.
                    // Render at the scaled resolution directly, then downsample ourselves instead.
                    renderScaleProperty.SetValue(renderPipelineAsset, 1f);
                    renderScaleOverridden = true;
                }

                int renderWidth = Mathf.Max(1, Mathf.RoundToInt(width * captureScale));
                int renderHeight = Mathf.Max(1, Mathf.RoundToInt(height * captureScale));
                renderTexture = RenderTexture.GetTemporary(
                    renderWidth,
                    renderHeight,
                    24,
                    RenderTextureFormat.ARGB32);
                renderTexture.filterMode = FilterMode.Bilinear;

                outputTexture = renderTexture;
                if (renderWidth != width || renderHeight != height)
                {
                    outputTexture = RenderTexture.GetTemporary(
                        width,
                        height,
                        0,
                        RenderTextureFormat.ARGB32);
                    outputTexture.filterMode = FilterMode.Bilinear;
                }

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                camera.targetTexture = renderTexture;
                camera.Render();

                if (outputTexture != renderTexture)
                    Graphics.Blit(renderTexture, outputTexture);

                RenderTexture.active = outputTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;

                if (outputTexture != null && outputTexture != renderTexture)
                    RenderTexture.ReleaseTemporary(outputTexture);
                if (renderTexture != null)
                    RenderTexture.ReleaseTemporary(renderTexture);
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);

                if (renderScaleOverridden)
                    renderScaleProperty.SetValue(renderPipelineAsset, previousRenderScale);
            }
        }

        private static bool TryGetUrpRenderScale(
            out object renderPipelineAsset,
            out PropertyInfo renderScaleProperty,
            out float renderScale)
        {
            renderPipelineAsset = GraphicsSettings.currentRenderPipeline;
            renderScaleProperty = null;
            renderScale = 1f;

            if (renderPipelineAsset == null)
                return false;

            Type assetType = renderPipelineAsset.GetType();
            Type currentType = assetType;
            while (currentType != null && currentType.FullName != UniversalRenderPipelineAssetTypeName)
                currentType = currentType.BaseType;

            if (currentType == null)
                return false;

            renderScaleProperty = assetType.GetProperty(
                "renderScale",
                BindingFlags.Instance | BindingFlags.Public);

            if (renderScaleProperty == null ||
                !renderScaleProperty.CanRead ||
                !renderScaleProperty.CanWrite ||
                renderScaleProperty.PropertyType != typeof(float))
            {
                renderScaleProperty = null;
                return false;
            }

            renderScale = (float)renderScaleProperty.GetValue(renderPipelineAsset);
            return true;
        }

        private static Vector2Int ResolveGameViewSize(Camera camera)
        {
            Vector2 size = TryGetMainGameViewSize();
            int width = Mathf.RoundToInt(size.x);
            int height = Mathf.RoundToInt(size.y);

            if (width > 0 && height > 0)
                return new Vector2Int(width, height);

            width = Mathf.RoundToInt(camera.pixelWidth);
            height = Mathf.RoundToInt(camera.pixelHeight);
            if (width > 0 && height > 0)
                return new Vector2Int(width, height);

            return new Vector2Int(FallbackWidth, FallbackHeight);
        }

        private static Vector2 TryGetMainGameViewSize()
        {
            try
            {
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                var method = gameViewType?.GetMethod(
                    "GetSizeOfMainGameView",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (method == null)
                    return Vector2.zero;

                object result = method.Invoke(null, null);
                return result is Vector2 size ? size : Vector2.zero;
            }
            catch
            {
                return Vector2.zero;
            }
        }

        private static string BuildOutputPath(Camera camera)
        {
            DirectoryInfo projectRootInfo = Directory.GetParent(Application.dataPath);
            string projectRoot = projectRootInfo != null ? projectRootInfo.FullName : Application.dataPath;
            string folder = Path.Combine(projectRoot, OutputFolder);
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

            if (string.IsNullOrEmpty(sceneName))
                sceneName = "UntitledScene";

            string fileName = string.Format(
                "{0}_{1}_{2}.png",
                SanitizeFileName(sceneName),
                SanitizeFileName(camera.name),
                DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            return Path.Combine(folder, fileName);
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');

            return value;
        }
    }

#if UNITY_2021_2_OR_NEWER
    [EditorToolbarElement(Id, typeof(SceneView))]
    public sealed class MainCameraScreenshotToolbarButton : EditorToolbarButton
    {
        public const string Id = "YAMO/Main Camera Screenshot";

        public MainCameraScreenshotToolbarButton()
        {
            text = "Shot";
            tooltip = "Capture Main Camera Screenshot";
            icon = EditorGUIUtility.IconContent("Camera Icon").image as Texture2D;
            clicked += MainCameraScreenshotCapture.Capture;
        }
    }

    [Overlay(typeof(SceneView), "YAMO Camera", true)]
    public sealed class YamoCameraToolbarOverlay : ToolbarOverlay
    {
        public YamoCameraToolbarOverlay()
            : base(MainCameraScreenshotToolbarButton.Id)
        {
        }
    }
#endif
}
