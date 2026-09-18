// lilToon "메인 컬러 / 알파" 의 색·HSV/Gamma·그라디언트 맵을 메인 텍스처에 구워 넣는다.
// (lilToon 인스펙터의 [굽기] 버튼을 저장 대화상자 없이 재현. Convert NiloToon 의 변환 직전 단계)
//
// 왜 필요한가:
//   - lilToon `_Color` 는 [lilHDR](lilToon 자체 드로어) → Unity 는 일반 sRGB 색으로 보고 리니어로 변환해 셰이더에 넘긴다.
//     NiloToon `_BaseColor` 는 Unity 정식 [HDR] → 변환 없이 그대로 쓴다. 같은 숫자를 옮겨도 NiloToon 쪽이 밝고 탁해진다.
//   - HSV/Gamma 는 NiloToon 이 자체 구현으로 옮겨 결과가 다를 수 있고, 그라디언트 맵은 아예 옮기지 않는다.
//   굽고 나면 색=흰색·HSVG=기본값이라 두 셰이더의 해석 차이가 사라진다.
//
// 안전장치:
//   - 원본 텍스처는 절대 덮어쓰지 않는다 (같은 폴더에 새 PNG 생성). 백업 머티리얼은 원본 텍스처를 계속 참조하므로 복구 가능.
//   - 굽기에 실패한 머티리얼은 아무것도 바꾸지 않고 건너뛴다.
//   - lilToon 어셈블리를 하드 참조하지 않는다 (베이커 셰이더를 이름으로만 찾음).

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public static class LilToonMainColorBaker
    {
        private const string BakerShaderName = "Hidden/ltsother_baker";
        private const string MaxTextureMapFileName = "YAMO_MaxMainTextureMap.tsv";
        private static readonly Vector4 DefaultHSVG = new Vector4(0f, 1f, 1f, 1f);

        public static bool IsAvailable => Shader.Find(BakerShaderName) != null;

        public static bool IsLilToon(Material mat)
        {
            return mat != null && mat.shader != null
                && mat.shader.name.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) >= 0
                && mat.HasProperty("_MainTexHSVG") && mat.HasProperty("_Color");
        }

        /// 색이 흰색이 아니거나 HSVG/그라디언트가 기본값이 아니면 굽기 대상 (lilToon 의 판정과 동일).
        public static bool NeedsBake(Material mat)
        {
            if (!IsLilToon(mat)) return false;
            if (mat.GetColor("_Color") != Color.white) return true;
            if (mat.GetVector("_MainTexHSVG") != DefaultHSVG) return true;
            if (mat.HasProperty("_MainGradationStrength") && mat.GetFloat("_MainGradationStrength") != 0f) return true;
            return false;
        }

        /// <summary>
        /// 굽기가 필요한 lilToon 머티리얼의 메인 텍스처를 구워 교체하고, 색/HSVG/그라디언트를 기본값으로 되돌린다.
        /// 구운 머티리얼 수를 반환. log 에 사람이 읽는 내역을 남긴다.
        /// </summary>
        public static int BakeAll(IEnumerable<Material> materials, List<string> log)
        {
            var bakerShader = Shader.Find(BakerShaderName);
            if (bakerShader == null)
            {
                log?.Add("⚠ lilToon 베이커 셰이더를 찾을 수 없어 색 굽기를 건너뜀");
                return 0;
            }

            // 같은 텍스처 + 같은 색 설정이면 구운 결과를 공유 (머티리얼 여러 개가 한 텍스처를 같은 색으로 쓰는 경우)
            var cache = new Dictionary<string, Texture2D>();
            int baked = 0;

            foreach (var mat in materials)
            {
                if (!NeedsBake(mat)) continue;
                try
                {
                    if (BakeOne(mat, bakerShader, cache, log)) baked++;
                }
                catch (System.Exception ex)
                {
                    log?.Add($"⚠ {mat.name}: 색 굽기 실패 — 머티리얼은 그대로 둠 ({ex.Message})");
                    Debug.LogException(ex, mat);
                }
            }

            if (baked > 0) AssetDatabase.SaveAssets();
            return baked;
        }

        private static bool BakeOne(Material mat, Shader bakerShader, Dictionary<string, Texture2D> cache, List<string> log)
        {
            var mainTex = mat.GetTexture("_MainTex") as Texture2D;
            string srcPath = mainTex != null ? AssetDatabase.GetAssetPath(mainTex) : null;
            bool hasSource = !string.IsNullOrEmpty(srcPath) && srcPath.StartsWith("Assets/");
            if (mainTex != null && !hasSource)
            {
                log?.Add($"⚠ {mat.name}: 메인 텍스처가 프로젝트 에셋이 아니라 색 굽기 건너뜀 ({srcPath})");
                return false;
            }

            Color color = mat.GetColor("_Color");
            Vector4 hsvg = mat.GetVector("_MainTexHSVG");
            float gradStrength = mat.HasProperty("_MainGradationStrength") ? mat.GetFloat("_MainGradationStrength") : 0f;
            Texture gradTex = mat.HasProperty("_MainGradationTex") ? mat.GetTexture("_MainGradationTex") : null;
            Texture adjustMask = mat.HasProperty("_MainColorAdjustMask") ? mat.GetTexture("_MainColorAdjustMask") : null;

            string key = (hasSource ? AssetDatabase.AssetPathToGUID(srcPath) : "(none)") + "|" + color + "|" + hsvg + "|" + gradStrength
                       + "|" + (gradTex ? gradTex.GetInstanceID() : 0) + "|" + (adjustMask ? adjustMask.GetInstanceID() : 0);

            if (!cache.TryGetValue(key, out Texture2D bakedAsset))
            {
                bakedAsset = RenderAndSave(mat, bakerShader, mainTex, srcPath, hasSource, color, hsvg, gradStrength, gradTex, adjustMask);
                if (bakedAsset == null)
                {
                    log?.Add($"⚠ {mat.name}: 구운 텍스처를 저장/로드하지 못해 건너뜀");
                    return false;
                }
                cache[key] = bakedAsset;
            }

            // lilToon 의 [굽기] 와 동일하게 머티리얼을 정리
            string oldName = hasSource ? Path.GetFileName(srcPath) : "(텍스처 없음)";
            mat.SetTexture("_MainTex", bakedAsset);
            mat.SetColor("_Color", Color.white);
            mat.SetVector("_MainTexHSVG", DefaultHSVG);
            if (mat.HasProperty("_MainGradationStrength")) mat.SetFloat("_MainGradationStrength", 0f);
            if (mat.HasProperty("_MainGradationTex")) mat.SetTexture("_MainGradationTex", null);
            EditorUtility.SetDirty(mat);

            string newPath = AssetDatabase.GetAssetPath(bakedAsset);
            UpdateMaxTextureMap(mat.name, newPath);

            log?.Add($"{mat.name}: 색 굽기 {oldName} → {Path.GetFileName(newPath)}  (색 #{ColorUtility.ToHtmlStringRGBA(color)}, HSVG {hsvg.x:0.##}/{hsvg.y:0.##}/{hsvg.z:0.##}/{hsvg.w:0.##}"
                     + (gradStrength != 0f ? $", 그라디언트 {gradStrength:0.##}" : "") + ")");
            return true;
        }

        private static Texture2D RenderAndSave(Material mat, Shader bakerShader, Texture2D mainTex, string srcPath, bool hasSource,
            Color color, Vector4 hsvg, float gradStrength, Texture gradTex, Texture adjustMask)
        {
            Texture2D src = null, outTex = null;
            var baker = new Material(bakerShader);
            try
            {
                // 텍스처가 없는 색만 있는 머티리얼은 4x4 흰색을 원본으로 삼아 단색 텍스처를 만든다
                src = hasSource ? LoadSourceTexture(mainTex, srcPath) : MakeWhite(4);
                if (src == null) return null;

                baker.SetColor("_Color", color);
                baker.SetVector("_MainTexHSVG", hsvg);
                baker.SetFloat("_MainGradationStrength", gradStrength);
                baker.SetTexture("_MainGradationTex", gradTex);
                baker.SetTexture("_MainColorAdjustMask", adjustMask);
                baker.SetTexture("_MainTex", src);

                // lilToonInspector.RunBake 와 동일한 렌더 경로 (기본 RenderTexture → ReadPixels)
                int w = src.width, h = src.height;
                outTex = new Texture2D(w, h);
                var prevRT = RenderTexture.active;
                var rt = RenderTexture.GetTemporary(w, h);
                Graphics.Blit(src, rt, baker);
                RenderTexture.active = rt;
                outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                outTex.Apply();
                RenderTexture.active = prevRT;
                RenderTexture.ReleaseTemporary(rt);

                // 저장 — 원본 옆(없으면 머티리얼 옆)에 새 파일로. 원본은 건드리지 않는다.
                string folder = Path.GetDirectoryName(hasSource ? srcPath : AssetDatabase.GetAssetPath(mat)).Replace('\\', '/');
                if (!hasSource)
                {
                    // 아바타 폴더 관례(<아바타>/Materials + <아바타>/Textures)면 텍스처 폴더에 둔다
                    string sibling = Path.GetDirectoryName(folder).Replace('\\', '/') + "/Textures";
                    if (AssetDatabase.IsValidFolder(sibling)) folder = sibling;
                }
                string baseName = hasSource ? Path.GetFileNameWithoutExtension(srcPath) : Sanitize(mat.name) + "_Color";
                string outPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{baseName}_Baked.png");
                File.WriteAllBytes(outPath, outTex.EncodeToPNG());
                AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceSynchronousImport);

                if (hasSource) CopyImporterSettings(srcPath, outPath);
                else
                {
                    // 단색 텍스처: 압축/밉맵이 색을 바꾸지 않도록
                    var ti = AssetImporter.GetAtPath(outPath) as TextureImporter;
                    if (ti != null) { ti.textureCompression = TextureImporterCompression.Uncompressed; ti.mipmapEnabled = false; ti.SaveAndReimport(); }
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
            }
            finally
            {
                Object.DestroyImmediate(baker);
                if (src != null) Object.DestroyImmediate(src);
                if (outTex != null) Object.DestroyImmediate(outTex);
            }
        }

        /// 임포트 크기/압축의 영향을 받지 않도록 PNG/JPG 는 원본 파일 바이트를 직접 읽는다 (lilTextureUtils.LoadTexture 와 동일한 방침).
        private static Texture2D LoadSourceTexture(Texture2D asset, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            Texture2D tex;
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                tex = new Texture2D(2, 2);
                if (!tex.LoadImage(File.ReadAllBytes(Path.GetFullPath(path)))) { Object.DestroyImmediate(tex); return null; }
            }
            else
            {
                // 그 밖의 포맷(tga/psd/exr...)은 임포트된 텍스처를 읽기 가능한 사본으로 복사
                var prevRT = RenderTexture.active;
                var rt = RenderTexture.GetTemporary(asset.width, asset.height);
                Graphics.Blit(asset, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(asset.width, asset.height);
                tex.ReadPixels(new Rect(0, 0, asset.width, asset.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prevRT;
                RenderTexture.ReleaseTemporary(rt);
            }
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        private static Texture2D MakeWhite(int size)
        {
            var tex = new Texture2D(size, size);
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        /// 구운 텍스처가 원본과 같은 임포트 설정(sRGB, 압축, 최대 크기, Wrap, 밉맵, AlphaIsTransparency...)을 갖게 한다.
        private static void CopyImporterSettings(string fromPath, string toPath)
        {
            var from = AssetImporter.GetAtPath(fromPath) as TextureImporter;
            var to = AssetImporter.GetAtPath(toPath) as TextureImporter;
            if (from == null || to == null) return;

            var settings = new TextureImporterSettings();
            from.ReadTextureSettings(settings);
            to.SetTextureSettings(settings);
            to.SetPlatformTextureSettings(from.GetDefaultPlatformTextureSettings());
            to.textureCompression = from.textureCompression;
            to.maxTextureSize = from.maxTextureSize;
            to.SaveAndReimport();
        }

        /// 3ds Max 자동 텍스처 매핑(TSV)이 같은 폴더에 있으면 해당 머티리얼의 메인 텍스처 파일명을 구운 파일로 갱신.
        private static void UpdateMaxTextureMap(string materialName, string newTexturePath)
        {
            string tsv = Path.GetDirectoryName(newTexturePath).Replace('\\', '/') + "/" + MaxTextureMapFileName;
            if (!File.Exists(tsv)) return;

            var lines = new List<string>(File.ReadAllLines(tsv));
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length == 0 || lines[i][0] == '#') continue;
                var cols = lines[i].Split('\t');
                if (cols.Length < 2 || !string.Equals(cols[0], materialName, System.StringComparison.OrdinalIgnoreCase)) continue;
                cols[1] = Path.GetFileName(newTexturePath);
                lines[i] = string.Join("\t", cols);
                found = true;
            }
            // 원래 메인 텍스처가 없어 행이 없던 머티리얼(단색 텍스처를 새로 만든 경우)은 행을 추가
            if (!found) lines.Add(materialName + "\t" + Path.GetFileName(newTexturePath));
            File.WriteAllLines(tsv, lines, new System.Text.UTF8Encoding(false));
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
