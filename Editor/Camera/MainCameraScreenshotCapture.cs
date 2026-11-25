using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

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
        private const string AlphaMenuPath = "Tools/YAMO/Camera/Screenshot Include Alpha";
        private const string AlphaPreferenceKey = "YAMO.MainCameraScreenshot.IncludeAlpha";
        private const string OutputFolder = "Assets/Screenshots";




        private static bool IncludeAlpha => EditorPrefs.GetBool(AlphaPreferenceKey, false);

        [MenuItem(AlphaMenuPath)]
        private static void ToggleIncludeAlpha()
        {
            EditorPrefs.SetBool(AlphaPreferenceKey, !IncludeAlpha);
        }

        [MenuItem(AlphaMenuPath, true)]
        private static bool ValidateIncludeAlpha()
        {
            Menu.SetChecked(AlphaMenuPath, IncludeAlpha);
            return true;
        }

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

            string outputPath = BuildOutputPath(camera);

            try
            {
                Vector2Int size = CaptureToPath(outputPath);
                Debug.Log($"[YAMO Screenshot] Saved Game View {size.x}x{size.y} ({(IncludeAlpha ? "RGBA" : "Opaque RGB")}): {outputPath}");
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

        /// <summary>Use the shortcut capture renderer with an explicit PNG destination.</summary>
        public static Vector2Int CaptureToPath(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Screenshot output path is empty.", nameof(outputPath));
            if (!Path.IsPathRooted(outputPath))
                outputPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, outputPath);
            return CaptureGameView(outputPath);
        }

        [Shortcut(ShortcutId)]
        private static void CaptureViaShortcut()
        {
            Capture();
        }

        // Read the displayed final frame. Camera.Render() uses a separate offscreen
        // render path and can change HDR, post effects, camera stacking and temporal effects.
        private static Vector2Int CaptureGameView(string outputPath)
        {
            RenderTexture source = GetDisplayedGameViewTexture();
            if (source == null || !source.IsCreated())
                throw new InvalidOperationException(
                    "Game View에 렌더된 화면이 없습니다. Game 탭을 열어 화면이 보이게 한 뒤 다시 촬영하세요.");

            var previousActive = RenderTexture.active;
            RenderTexture resolved = null;
            Texture2D texture = null;
            try
            {
                RenderTexture readable = source;
                if (source.antiAliasing > 1)
                {
                    var descriptor = source.descriptor;
                    descriptor.msaaSamples = 1;
                    descriptor.depthBufferBits = 0;
                    descriptor.bindMS = false;
                    resolved = RenderTexture.GetTemporary(descriptor);
                    source.ResolveAntiAliasedSurface(resolved);
                    readable = resolved;
                }

                RenderTexture.active = readable;
                texture = new Texture2D(source.width, source.height,
                    IncludeAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                // The Editor displays its final render target with a vertical UV flip
                // on top-origin graphics backends (D3D/Metal). PNG rows are bottom-origin.
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    var pixels = texture.GetPixels32();
                    int rowWidth = source.width;
                    for (int y = 0; y < source.height / 2; y++)
                    {
                        int top = y * rowWidth;
                        int bottom = (source.height - 1 - y) * rowWidth;
                        for (int x = 0; x < rowWidth; x++)
                        {
                            Color32 pixel = pixels[top + x];
                            pixels[top + x] = pixels[bottom + x];
                            pixels[bottom + x] = pixel;
                        }
                    }
                    texture.SetPixels32(pixels);
                }
                texture.Apply(false, false);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                return new Vector2Int(source.width, source.height);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (resolved != null)
                    RenderTexture.ReleaseTemporary(resolved);
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static RenderTexture GetDisplayedGameViewTexture()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
                throw new NotSupportedException("이 Unity 버전에서 Game View를 찾을 수 없습니다.");

            // Prefer the Game View the user is looking at; otherwise use the main one.
            EditorWindow view = EditorWindow.focusedWindow;
            if (view == null || !gameViewType.IsInstanceOfType(view))
            {
                var playModeView = gameViewType.BaseType;
                var getMain = playModeView?.GetMethod("GetMainPlayModeView",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                view = getMain?.Invoke(null, null) as EditorWindow;
            }

            if (view == null || !gameViewType.IsInstanceOfType(view))
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
                {
                    var window = candidate as EditorWindow;
                    if (window != null && ReadGameViewTexture(window) != null)
                        return ReadGameViewTexture(window);
                }
                return null;
            }

            return ReadGameViewTexture(view);
        }

        private static RenderTexture ReadGameViewTexture(EditorWindow view)
        {
            // PlayModeView owns the final target in current Unity versions.
            // Keep the older GameView field as a compatibility fallback.
            foreach (string fieldName in new[] { "m_TargetTexture", "m_RenderTexture" })
            {
                for (Type type = view.GetType(); type != null; type = type.BaseType)
                {
                    var field = type.GetField(fieldName,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                        BindingFlags.DeclaredOnly);
                    var target = field?.GetValue(view) as RenderTexture;
                    if (target != null && target.IsCreated())
                        return target;
                }
            }
            return null;
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
