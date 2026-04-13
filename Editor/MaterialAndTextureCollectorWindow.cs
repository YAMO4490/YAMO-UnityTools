using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public class MaterialAndTextureTool : EditorWindow
{
    // =====================================================
    // 섹션 1: 머테리얼 / 텍스처 관리
    // =====================================================
    private GameObject targetPrefab;
    private string materialOutputPath = "Assets/DuplicatedMaterials";
    private string textureOutputPath = "Assets/DuplicatedTextures";
	private string moveOutputPath = "Assets/CollectedAssets";

    private Dictionary<string, List<Material>> duplicateMaterialMap = new Dictionary<string, List<Material>>();
    private Dictionary<string, List<Texture>> duplicateTextureMap = new Dictionary<string, List<Texture>>();
    private Dictionary<Material, Material> materialCopies = new Dictionary<Material, Material>();
    private Dictionary<Texture, Texture> textureCopies = new Dictionary<Texture, Texture>();
    private HashSet<Material> collectedMaterials = new HashSet<Material>();
    private HashSet<Texture> collectedTextures = new HashSet<Texture>();

    // =====================================================
    // 섹션 2: PSD → PNG 변환
    // =====================================================
    // 스캔된 PSD 파일 1개분의 정보
    private class PsdEntry
    {
        public string relPath;
        public int srcWidth;
        public int srcHeight;
        public bool include = true; // false면 변환 대상에서 제외

        public bool IsNonSquare => srcWidth != srcHeight;
        public bool NeedsResize(int limit) => srcWidth > limit || srcHeight > limit;
        public string DimLabel => $"{srcWidth}×{srcHeight}";
    }

    private string psdSearchPath = "Assets";
    private bool psdResizeOver2048 = false;
    private List<PsdEntry> psdFoundEntries = new List<PsdEntry>();
    private List<string> psdResultLog = new List<string>();
    private bool psdScanned = false;

    // =====================================================
    // 섹션 3: 텍스처 리사이즈 (2048 초과)
    // =====================================================
    // 스캔된 대형 텍스처 1개분의 정보
    private class TexEntry
    {
        public string relPath;
        public int srcWidth;
        public int srcHeight;
        public string outputExt;  // 출력 확장자 (포맷 변환 시 입력과 다름)
        public int targetWidth;
        public int targetHeight;
        public bool include = true;

        // 스캔 시점에 저장 — PrepareTexResizeImporters가 meta를 덮어쓰기 전의 원본
        public string originalMetaContent;
        // 스캔 시점에 저장 — PrepareTexResizeImporters 이후 importer 설정이 바뀌어도 정확한 값 유지
        public bool isLinearSource;

        public bool NeedsResize(int limit) => srcWidth > limit || srcHeight > limit;
        public string SrcDimLabel => $"{srcWidth}×{srcHeight}";
        public string DstDimLabel => $"{targetWidth}×{targetHeight}";
        public bool IsFormatChange => outputExt != Path.GetExtension(relPath).ToLower();
    }

    // 리사이즈 대상 확장자 목록 (.psd 제외 — 섹션 2에서 별도 처리)
    private static readonly string[] ResizableTexExts =
    {
        ".png", ".jpg", ".jpeg", ".exr", ".tga", ".bmp", ".tif", ".tiff", ".hdr", ".gif"
    };

    private string texResizePath = "Assets";
    private int texJpgQuality = 85;
    private List<TexEntry> texResizeEntries = new List<TexEntry>();
    private List<string> texResizeLog = new List<string>();
    private bool texResizeScanned = false;

    // =====================================================
    // 공통
    // =====================================================
    private Vector2 scroll;
    private bool sec1Foldout = true;
    private bool sec2Foldout = true;
    private bool sec3Foldout = true;

    // 수정됨: "&" -> "And"로 원복하여 기존 경로와 일치시킴
    [MenuItem("Tools/Material And Texture Tool")]
    public static void ShowWindow()
    {
        // 토글 기능: 이미 열려있으면 닫고, 없으면 엽니다.
        if (HasOpenInstances<MaterialAndTextureTool>())
        {
            GetWindow<MaterialAndTextureTool>().Close();
        }
        else
        {
            var window = GetWindow<MaterialAndTextureTool>("MatTex Tool");
            window.minSize = new Vector2(600, 300);
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("\uD83C\uDF1F 머테리얼 & 텍스처 유틸리티", EditorStyles.boldLabel);

        scroll = EditorGUILayout.BeginScrollView(scroll);

        // ▼▼▼ 섹션 1: 머테리얼 / 텍스처 관리 ▼▼▼
        sec1Foldout = DrawFoldoutSectionHeader(sec1Foldout, "📦  머테리얼 / 텍스처 관리");
        if (sec1Foldout)
        {
            targetPrefab = (GameObject)EditorGUILayout.ObjectField("\uD83D\uDCE6 타겟 프리팹", targetPrefab, typeof(GameObject), true);
            EditorGUILayout.BeginHorizontal();
            materialOutputPath = EditorGUILayout.TextField("\uD83D\uDCC2 머테리얼 저장 경로", materialOutputPath);
            if (GUILayout.Button("선택", GUILayout.Width(60)))
            {
                var selected = ChooseFolder(materialOutputPath);
                if (!string.IsNullOrEmpty(selected)) materialOutputPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            textureOutputPath = EditorGUILayout.TextField("\uD83D\uDCC2 텍스처 저장 경로", textureOutputPath);
            if (GUILayout.Button("선택", GUILayout.Width(60)))
            {
                var selected = ChooseFolder(textureOutputPath);
                if (!string.IsNullOrEmpty(selected)) textureOutputPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            moveOutputPath = EditorGUILayout.TextField("\uD83D\uDCE6 이동 대상 경로", moveOutputPath);
            if (GUILayout.Button("선택", GUILayout.Width(60)))
            {
                var selected = ChooseFolder(moveOutputPath);
                if (!string.IsNullOrEmpty(selected)) moveOutputPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            if (GUILayout.Button("\uD83D\uDCCB 중복 이름 검사")) CollectDuplicates();
            if (GUILayout.Button("\uD83D\uDD04 중복 이름 자동 변경")) RenameDuplicateAssets();
            if (GUILayout.Button("\uD83D\uDD04 머테리얼 및 텍스처 복사")) DuplicateMaterialsAndTextures();
            if (GUILayout.Button("\uD83D\uDCE6 중복 없으면 참조 자산 이동")) MoveReferencedAssetsIfNoDuplicates();

            // 섹션 1 결과
            GUILayout.Space(6);
            GUILayout.Label("\u26A0\uFE0F 중복된 이름의 머테리얼", EditorStyles.boldLabel);
            DrawDuplicateList(duplicateMaterialMap);

            GUILayout.Space(10);
            GUILayout.Label("\u26A0\uFE0F 중복된 이름의 텍스처", EditorStyles.boldLabel);
            DrawDuplicateList(duplicateTextureMap);

            GUILayout.Space(10);
            GUILayout.Label("\uD83D\uDD0D 참조된 모든 머테리얼", EditorStyles.boldLabel);
            foreach (var mat in collectedMaterials)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(mat.name, GUILayout.Width(200)))
                    EditorGUIUtility.PingObject(mat);
                EditorGUILayout.ObjectField(mat, typeof(Material), false);
                EditorGUILayout.EndHorizontal();
            }

            GUILayout.Space(10);
            GUILayout.Label("\uD83D\uDD0D 참조된 모든 텍스처", EditorStyles.boldLabel);
            foreach (var tex in collectedTextures)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(tex.name, GUILayout.Width(200)))
                    EditorGUIUtility.PingObject(tex);
                EditorGUILayout.ObjectField(tex, typeof(Texture), false);
                EditorGUILayout.EndHorizontal();
            }
        }
        // ▲▲▲ 섹션 1 ▲▲▲

        // ▼▼▼ 섹션 2: PSD → PNG 변환 ▼▼▼
        sec2Foldout = DrawFoldoutSectionHeader(sec2Foldout, "🖼️  PSD → PNG 변환 (GUID 참조 보존)");
        if (sec2Foldout)
        {
            EditorGUILayout.HelpBox(
                "PSD 파일을 PNG로 변환하면서 .meta GUID를 이식합니다.\n" +
                "머테리얼의 텍스처 참조관계가 깨지지 않고 그대로 유지됩니다.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            psdSearchPath = EditorGUILayout.TextField("🔍 검색 경로", psdSearchPath);
            if (GUILayout.Button("선택", GUILayout.Width(60)))
            {
                var selected = ChooseFolder(psdSearchPath);
                if (!string.IsNullOrEmpty(selected))
                {
                    psdSearchPath = selected;
                    psdScanned = false;
                    psdFoundEntries.Clear();
                    psdResultLog.Clear();
                }
            }
            EditorGUILayout.EndHorizontal();

            psdResizeOver2048 = EditorGUILayout.Toggle("📐 2048 초과 시 2048로 리사이즈", psdResizeOver2048);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("🔍 PSD 파일 스캔")) ScanPSDFiles();

            int psdConvertCount = 0;
            foreach (var e in psdFoundEntries) if (e.include) psdConvertCount++;
            GUI.enabled = psdScanned && psdConvertCount > 0;
            if (GUILayout.Button($"🔄 PNG로 변환 실행 ({psdConvertCount}개)"))
            {
                if (EditorUtility.DisplayDialog(
                    "변환 확인",
                    $"{psdConvertCount}개의 PSD 파일을 PNG로 변환합니다.\n" +
                    "원본 PSD 파일은 삭제됩니다.\n\n계속하시겠습니까?",
                    "변환", "취소"))
                {
                    ConvertAllPSDtoPNG();
                }
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // 스캔 결과 헤더 (비정방형 경고 + 일괄 제외 버튼)
            if (psdScanned && psdResultLog.Count == 0)
            {
                if (psdFoundEntries.Count == 0)
                {
                    EditorGUILayout.HelpBox("해당 경로에 PSD 파일이 없습니다.", MessageType.Info);
                }
                else
                {
                    int nonSquareCount = 0;
                    foreach (var e in psdFoundEntries) if (e.IsNonSquare) nonSquareCount++;

                    EditorGUILayout.BeginHorizontal();
                    string psdHeader = $"📋 발견된 PSD 파일: {psdFoundEntries.Count}개";
                    if (nonSquareCount > 0) psdHeader += $"   ⚠️ 비정방형: {nonSquareCount}개";
                    GUILayout.Label(psdHeader, EditorStyles.boldLabel);
                    if (nonSquareCount > 0)
                    {
                        if (GUILayout.Button("비정방형 전체 제외", GUILayout.Width(120)))
                            foreach (var e in psdFoundEntries) { if (e.IsNonSquare) e.include = false; }
                        if (GUILayout.Button("전체 포함 복구", GUILayout.Width(100)))
                            foreach (var e in psdFoundEntries) e.include = true;
                    }
                    EditorGUILayout.EndHorizontal();

                    if (nonSquareCount > 0)
                        EditorGUILayout.HelpBox(
                            "⚠️ 주황색으로 표시된 파일은 가로/세로 비율이 1:1이 아닙니다.\n" +
                            "2048 리사이즈 옵션 사용 시 비율이 유지된 채로 축소됩니다.",
                            MessageType.Warning);
                }
            }

            // 섹션 2 파일 목록
            if (psdScanned && psdResultLog.Count == 0 && psdFoundEntries.Count > 0)
            {
                foreach (var entry in psdFoundEntries)
                {
                    EditorGUILayout.BeginHorizontal();
                    entry.include = EditorGUILayout.Toggle(entry.include, GUILayout.Width(16));

                    GUIStyle psdLabelStyle;
                    if (!entry.include) psdLabelStyle = GetGrayStyle();
                    else if (entry.IsNonSquare) psdLabelStyle = GetWarningStyle();
                    else psdLabelStyle = EditorStyles.miniLabel;

                    string psdDimInfo = $"[{entry.DimLabel}]";
                    if (entry.IsNonSquare) psdDimInfo += " ⚠️비정방형";
                    if (psdResizeOver2048 && entry.NeedsResize(2048)) psdDimInfo += " →2048";
                    if (!entry.include) psdDimInfo += " (제외)";

                    GUILayout.Label($"{Path.GetFileName(entry.relPath)}  {psdDimInfo}", psdLabelStyle);

                    if (entry.include)
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.relPath);
                        if (asset != null)
                            EditorGUILayout.ObjectField(asset, typeof(Texture2D), false, GUILayout.Width(80));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // 섹션 2 변환 결과 로그
            if (psdResultLog.Count > 0)
            {
                GUILayout.Label("📊 변환 결과", EditorStyles.boldLabel);
                foreach (var log in psdResultLog)
                    GUILayout.Label(log, EditorStyles.miniLabel);
            }
        }
        // ▲▲▲ 섹션 2 ▲▲▲

        // ▼▼▼ 섹션 3: 텍스처 리사이즈 (2048 초과) ▼▼▼
        sec3Foldout = DrawFoldoutSectionHeader(sec3Foldout, "📐  텍스처 리사이즈 (2048 초과)");
        if (sec3Foldout)
        {
            EditorGUILayout.HelpBox(
                "2048을 초과하는 텍스처를 비율 유지하며 2048 이하로 축소합니다.\n" +
                "지원: PNG · JPG · EXR · TGA · BMP · TIF · HDR · GIF\n" +
                "BMP·TIF·GIF는 PNG로, HDR은 EXR로 포맷이 변환되며 GUID가 보존됩니다.\n" +
                "⚠️ JPG는 재인코딩 시 화질 손실이 누적될 수 있습니다.",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            texResizePath = EditorGUILayout.TextField("🔍 검색 경로", texResizePath);
            if (GUILayout.Button("선택", GUILayout.Width(60)))
            {
                var selected = ChooseFolder(texResizePath);
                if (!string.IsNullOrEmpty(selected))
                {
                    texResizePath = selected;
                    texResizeScanned = false;
                    texResizeEntries.Clear();
                    texResizeLog.Clear();
                }
            }
            EditorGUILayout.EndHorizontal();

            texJpgQuality = EditorGUILayout.IntSlider("JPG 재인코딩 품질", texJpgQuality, 1, 100);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("🔍 대형 텍스처 스캔")) ScanLargeTextures();

            int texResizeCount = 0;
            foreach (var e in texResizeEntries) if (e.include) texResizeCount++;
            GUI.enabled = texResizeScanned && texResizeCount > 0;
            if (GUILayout.Button($"📐 리사이즈 실행 ({texResizeCount}개)"))
            {
                if (EditorUtility.DisplayDialog(
                    "리사이즈 확인",
                    $"{texResizeCount}개의 텍스처를 2048 이하로 리사이즈합니다.\n" +
                    "원본 파일은 덮어씌워집니다.\n\n계속하시겠습니까?",
                    "실행", "취소"))
                {
                    ResizeAllTextures();
                }
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // 스캔 결과 헤더 (포맷 변환 경고)
            if (texResizeScanned && texResizeLog.Count == 0)
            {
                if (texResizeEntries.Count == 0)
                {
                    EditorGUILayout.HelpBox("2048을 초과하는 텍스처가 없습니다.", MessageType.Info);
                }
                else
                {
                    int formatChangeCount = 0;
                    foreach (var e in texResizeEntries) if (e.IsFormatChange) formatChangeCount++;

                    EditorGUILayout.BeginHorizontal();
                    string texHeader = $"📋 대형 텍스처: {texResizeEntries.Count}개";
                    if (formatChangeCount > 0) texHeader += $"   🔁 포맷 변환: {formatChangeCount}개";
                    GUILayout.Label(texHeader, EditorStyles.boldLabel);
                    if (GUILayout.Button("전체 포함 복구", GUILayout.Width(100)))
                        foreach (var e in texResizeEntries) e.include = true;
                    EditorGUILayout.EndHorizontal();

                    if (formatChangeCount > 0)
                        EditorGUILayout.HelpBox(
                            "🔁 파란색으로 표시된 파일은 포맷이 변환됩니다 (예: BMP→PNG, HDR→EXR).\n" +
                            "변환 시 GUID가 보존되어 머테리얼 참조관계가 유지됩니다.",
                            MessageType.Warning);
                }
            }

            // 섹션 3 파일 목록
            if (texResizeScanned && texResizeLog.Count == 0 && texResizeEntries.Count > 0)
            {
                foreach (var entry in texResizeEntries)
                {
                    EditorGUILayout.BeginHorizontal();
                    entry.include = EditorGUILayout.Toggle(entry.include, GUILayout.Width(16));

                    GUIStyle texLabelStyle;
                    if (!entry.include) texLabelStyle = GetGrayStyle();
                    else if (entry.IsFormatChange) texLabelStyle = GetBlueStyle();
                    else texLabelStyle = EditorStyles.miniLabel;

                    string inputExt = Path.GetExtension(entry.relPath).ToLower();
                    string dimInfo = $"[{entry.SrcDimLabel} → {entry.DstDimLabel}]";
                    if (entry.IsFormatChange) dimInfo += $" 🔁{inputExt}→{entry.outputExt}";
                    if (!entry.include) dimInfo += " (제외)";

                    GUILayout.Label($"{Path.GetFileName(entry.relPath)}  {dimInfo}", texLabelStyle);

                    if (entry.include)
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.relPath);
                        if (asset != null)
                            EditorGUILayout.ObjectField(asset, typeof(Texture2D), false, GUILayout.Width(80));
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // 섹션 3 리사이즈 결과 로그
            if (texResizeLog.Count > 0)
            {
                GUILayout.Label("📊 리사이즈 결과", EditorStyles.boldLabel);
                foreach (var log in texResizeLog)
                    GUILayout.Label(log, EditorStyles.miniLabel);
            }
        }
        // ▲▲▲ 섹션 3 ▲▲▲

        EditorGUILayout.EndScrollView();
    }

    // =====================================================
    // 섹션 1 메서드: 머테리얼 / 텍스처 관리 (기존 코드 유지)
    // =====================================================

    void CollectDuplicates()
    {
        duplicateMaterialMap.Clear();
        duplicateTextureMap.Clear();
        collectedMaterials.Clear();
        collectedTextures.Clear();

        if (targetPrefab == null) return;

        HashSet<Material> seenMaterials = new HashSet<Material>();
        HashSet<Texture> seenTextures = new HashSet<Texture>();

        var renderers = targetPrefab.GetComponentsInChildren<Renderer>(true);

        foreach (var renderer in renderers)
        {
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat == null || seenMaterials.Contains(mat)) continue;
                seenMaterials.Add(mat);
                collectedMaterials.Add(mat);

                if (!duplicateMaterialMap.ContainsKey(mat.name))
                    duplicateMaterialMap[mat.name] = new List<Material>();
                duplicateMaterialMap[mat.name].Add(mat);

                Shader shader = mat.shader;
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    Texture tex = mat.GetTexture(propName);
                    if (tex == null || seenTextures.Contains(tex)) continue;
                    seenTextures.Add(tex);
                    collectedTextures.Add(tex);

                    if (!duplicateTextureMap.ContainsKey(tex.name))
                        duplicateTextureMap[tex.name] = new List<Texture>();
                    duplicateTextureMap[tex.name].Add(tex);
                }
            }
        }
    }

    void DrawDuplicateList<T>(Dictionary<string, List<T>> map) where T : Object
    {
        foreach (var pair in map)
        {
            if (pair.Value.Count < 2) continue;
            GUILayout.Label("\u26A0\uFE0F " + pair.Key + " (" + pair.Value.Count + "개)", GetRedStyle());
            foreach (var obj in pair.Value)
            {
                EditorGUILayout.ObjectField(obj, typeof(T), false);
            }
        }
    }

	void RenameDuplicateAssets()
    {
		RenameAssetGroup(duplicateMaterialMap);
		RenameAssetGroup(duplicateTextureMap);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
		CollectDuplicates();
    }

	void RenameAssetGroup<T>(Dictionary<string, List<T>> map) where T : Object
    {
		foreach (var pair in map)
        {
            if (pair.Value.Count < 2) continue;
			// 경로로 안정 정렬하여 첫 번째 항목은 유지, 이후 _1, _2... 부여
			List<T> items = new List<T>(pair.Value);
			items.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
			for (int i = 0; i < items.Count; i++)
			{
				if (i == 0) continue; // 첫 번째는 원래 이름 유지
				var obj = items[i];
				string path = AssetDatabase.GetAssetPath(obj);
				if (string.IsNullOrEmpty(path)) continue;
				string newName = pair.Key + "_" + i;
				string result = AssetDatabase.RenameAsset(path, newName);
				if (!string.IsNullOrEmpty(result))
				{
					Debug.LogWarning("리네이밍 실패: " + result);
				}
			}
        }
    }

    void DuplicateMaterialsAndTextures()
    {
        if (targetPrefab == null) return;

        materialCopies.Clear();
        textureCopies.Clear();

		EnsureFolderExists(materialOutputPath);
		EnsureFolderExists(textureOutputPath);

        Renderer[] renderers = targetPrefab.GetComponentsInChildren<Renderer>(true);

        foreach (var renderer in renderers)
        {
            Material[] newMats = new Material[renderer.sharedMaterials.Length];

            for (int i = 0; i < newMats.Length; i++)
            {
                Material orig = renderer.sharedMaterials[i];
                if (orig == null) continue;

                if (!materialCopies.ContainsKey(orig))
                {
                    Material newMat = new Material(orig);
                    string matPath = AssetDatabase.GenerateUniqueAssetPath(materialOutputPath + "/" + orig.name + "_Copy.mat");
                    AssetDatabase.CreateAsset(newMat, matPath);
                    materialCopies[orig] = newMat;
                    CopyTextures(orig, newMat);
                    EditorUtility.SetDirty(newMat);
                }
                newMats[i] = materialCopies[orig];
            }

            Undo.RecordObject(renderer, "Apply Copied Materials");
            renderer.sharedMaterials = newMats;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    void CopyTextures(Material original, Material copy)
    {
        Shader shader = original.shader;
        int count = ShaderUtil.GetPropertyCount(shader);

        string[] maskProps = { "_MaskMap", "_OcclusionMap", "_DetailMask", "_RoughnessMap", "_MetallicGlossMap" };

        for (int i = 0; i < count; i++)
        {
            string prop = ShaderUtil.GetPropertyName(shader, i);
            Texture tex = original.GetTexture(prop);
            if (tex == null) continue;

            bool isTexEnv = ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv;
            bool isMask = System.Array.IndexOf(maskProps, prop) >= 0;

            if (isTexEnv || isMask)
            {
                if (!textureCopies.ContainsKey(tex))
                {
                    string path = AssetDatabase.GetAssetPath(tex);
                    string newPath = AssetDatabase.GenerateUniqueAssetPath(textureOutputPath + "/" + tex.name + "_Copy" + Path.GetExtension(path));
                    AssetDatabase.CopyAsset(path, newPath);
                    Texture newTex = AssetDatabase.LoadAssetAtPath<Texture>(newPath);
                    textureCopies[tex] = newTex;
                }

                copy.SetTexture(prop, textureCopies[tex]);
                EditorUtility.SetDirty(copy);
            }
        }
    }

	bool HasDuplicates()
	{
		foreach (var kv in duplicateMaterialMap)
		{
			if (kv.Value != null && kv.Value.Count > 1) return true;
		}
		foreach (var kv in duplicateTextureMap)
		{
			if (kv.Value != null && kv.Value.Count > 1) return true;
		}
		return false;
	}

	void MoveReferencedAssetsIfNoDuplicates()
	{
		if (targetPrefab == null)
		{
			EditorUtility.DisplayDialog("알림", "타겟 프리팹을 먼저 지정하세요.", "확인");
			return;
		}

		// 최신 상태 보장
		CollectDuplicates();
		if (HasDuplicates())
		{
			EditorUtility.DisplayDialog("중복 발견", "중복된 머테리얼 또는 텍스처 이름이 있습니다. 먼저 중복을 해결하세요.", "확인");
			return;
		}

		EnsureFolderExists(moveOutputPath);
		string materialsFolder = moveOutputPath.TrimEnd('/', '\\') + "/Materials";
		string texturesFolder = moveOutputPath.TrimEnd('/', '\\') + "/Textures";
		EnsureFolderExists(materialsFolder);
		EnsureFolderExists(texturesFolder);

		// 이동 대상 수집: 현재 수집된 세트 사용 (분리)
		List<string> materialPaths = new List<string>();
		foreach (var mat in collectedMaterials)
		{
			string p = AssetDatabase.GetAssetPath(mat);
			if (!string.IsNullOrEmpty(p)) materialPaths.Add(p);
		}
		List<string> texturePaths = new List<string>();
		foreach (var tex in collectedTextures)
		{
			string p = AssetDatabase.GetAssetPath(tex);
			if (!string.IsNullOrEmpty(p)) texturePaths.Add(p);
		}

		int totalToMove = materialPaths.Count + texturePaths.Count;
		if (totalToMove == 0)
		{
			EditorUtility.DisplayDialog("알림", "이동할 참조 자산이 없습니다.", "확인");
			return;
		}

		int moved = 0;
		foreach (var srcPath in materialPaths)
		{
			string fileName = Path.GetFileName(srcPath);
			string dstPath = AssetDatabase.GenerateUniqueAssetPath(materialsFolder + "/" + fileName);
			string err = AssetDatabase.MoveAsset(srcPath, dstPath);
			if (string.IsNullOrEmpty(err)) moved++;
			else Debug.LogWarning($"이동 실패(머테리얼): {srcPath} -> {dstPath} : {err}");
		}
		foreach (var srcPath in texturePaths)
		{
			string fileName = Path.GetFileName(srcPath);
			string dstPath = AssetDatabase.GenerateUniqueAssetPath(texturesFolder + "/" + fileName);
			string err = AssetDatabase.MoveAsset(srcPath, dstPath);
			if (string.IsNullOrEmpty(err)) moved++;
			else Debug.LogWarning($"이동 실패(텍스처): {srcPath} -> {dstPath} : {err}");
		}

		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		CollectDuplicates();
		EditorUtility.DisplayDialog("완료", $"이동 완료: {moved}/{totalToMove}", "확인");
	}

	void EnsureFolderExists(string projectRelativePath)
	{
		if (string.IsNullOrEmpty(projectRelativePath)) return;
		projectRelativePath = projectRelativePath.Replace('\\', '/');
		if (!projectRelativePath.StartsWith("Assets"))
		{
			Debug.LogWarning($"프로젝트 상대 경로가 아닙니다: {projectRelativePath}. 'Assets'로 시작해야 합니다.");
			return;
		}

		if (AssetDatabase.IsValidFolder(projectRelativePath)) return;

		string[] parts = projectRelativePath.Split('/');
		string current = parts[0]; // Assets
		for (int i = 1; i < parts.Length; i++)
		{
			string next = current + "/" + parts[i];
			if (!AssetDatabase.IsValidFolder(next))
			{
				AssetDatabase.CreateFolder(current, parts[i]);
			}
			current = next;
		}
	}

    // =====================================================
    // 섹션 2 메서드: PSD → PNG 변환
    // =====================================================

    // 지정 경로 아래의 모든 PSD 파일을 탐색하고 소스 해상도를 읽어둠
    void ScanPSDFiles()
    {
        psdFoundEntries.Clear();
        psdResultLog.Clear();
        psdScanned = false;

        string absSearchPath = ProjectRelativeToAbsolute(psdSearchPath);
        if (!Directory.Exists(absSearchPath))
        {
            EditorUtility.DisplayDialog("오류", $"경로를 찾을 수 없습니다:\n{psdSearchPath}", "확인");
            return;
        }

        string[] psdFiles = Directory.GetFiles(absSearchPath, "*.psd", SearchOption.AllDirectories);
        foreach (string absPath in psdFiles)
        {
            string relPath = GetProjectRelativePath(absPath);
            if (string.IsNullOrEmpty(relPath)) continue;

            var entry = new PsdEntry { relPath = relPath };

            // TextureImporter에서 소스 해상도 읽기 (임포트 제한 전의 원본 크기)
            var importer = AssetImporter.GetAtPath(relPath) as TextureImporter;
            if (importer != null)
                importer.GetSourceTextureWidthAndHeight(out entry.srcWidth, out entry.srcHeight);

            psdFoundEntries.Add(entry);
        }

        psdScanned = true;
        int nonSquareCount = 0;
        foreach (var e in psdFoundEntries) if (e.IsNonSquare) nonSquareCount++;
        Debug.Log($"[PSD→PNG] 스캔 완료 — {psdFoundEntries.Count}개 발견 / 비정방형: {nonSquareCount}개 ({psdSearchPath})");
        Repaint();
    }

    // PSD → PNG 일괄 변환 (GUID 보존)
    void ConvertAllPSDtoPNG()
    {
        psdResultLog.Clear();
        int successCount = 0;
        int skipCount = 0;
        int errorCount = 0;

        // 변환 전 일괄 임포터 설정 (Read/Write 활성화 + 압축 해제 + 필요시 리사이즈 설정)
        // SaveAndReimport는 AssetDatabase.StartAssetEditing 밖에서 실행해야 함
        PrepareImporters();

        var targets = new List<PsdEntry>();
        foreach (var e in psdFoundEntries) if (e.include) targets.Add(e);

        for (int i = 0; i < targets.Count; i++)
        {
            var entry = targets[i];
            string psdRelPath = entry.relPath;
            string psdAbsPath = ProjectRelativeToAbsolute(psdRelPath);
            string pngRelPath = Path.ChangeExtension(psdRelPath, ".png").Replace('\\', '/');
            string pngAbsPath = ProjectRelativeToAbsolute(pngRelPath);

            EditorUtility.DisplayProgressBar(
                "PSD → PNG 변환 중",
                Path.GetFileName(psdRelPath),
                (float)i / targets.Count);

            // 동일 이름 PNG가 이미 존재하면 스킵
            if (File.Exists(pngAbsPath))
            {
                Debug.LogWarning($"[PSD→PNG] 스킵 (PNG 이미 존재): {psdRelPath}");
                psdResultLog.Add($"⚠️ 스킵: {Path.GetFileName(psdRelPath)} — 동일 이름 PNG 이미 존재");
                skipCount++;
                continue;
            }

            // .meta 파일 존재 확인 및 내용 읽기
            string metaAbsPath = psdAbsPath + ".meta";
            if (!File.Exists(metaAbsPath))
            {
                Debug.LogWarning($"[PSD→PNG] .meta 파일 없음: {psdRelPath}");
                psdResultLog.Add($"⚠️ 스킵: {Path.GetFileName(psdRelPath)} — .meta 파일 없음");
                skipCount++;
                continue;
            }
            string metaContent = File.ReadAllText(metaAbsPath);

            // GUID 확인 (정보용 로그)
            var guidMatch = Regex.Match(metaContent, @"guid: ([a-f0-9]+)");
            string guid = guidMatch.Success ? guidMatch.Groups[1].Value : "(GUID 없음)";

            // 텍스처 로드 (PrepareImporters에서 maxTextureSize가 설정됐다면 이미 축소된 상태로 로드됨)
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(psdRelPath);
            if (tex == null)
            {
                Debug.LogWarning($"[PSD→PNG] 텍스처 로드 실패: {psdRelPath}");
                psdResultLog.Add($"❌ 실패: {Path.GetFileName(psdRelPath)} — 텍스처 로드 불가");
                errorCount++;
                continue;
            }

            // GPU blit으로 압축 포맷 → 비압축 RGBA32 변환 후 인코딩
            // (플랫폼별 override가 남아있어 텍스처가 여전히 압축돼 있어도 GPU에서 읽기 가능)
            var psdImporter = AssetImporter.GetAtPath(psdRelPath) as TextureImporter;
            bool isLinearPsd = psdImporter != null && !psdImporter.sRGBTexture;
            var readableTex = ResizeTexture(tex, tex.width, tex.height, isLinearPsd);
            byte[] pngBytes = readableTex.EncodeToPNG();
            if (pngBytes == null || pngBytes.Length == 0)
            {
                Debug.LogWarning($"[PSD→PNG] PNG 인코딩 실패: {psdRelPath}");
                psdResultLog.Add($"❌ 실패: {Path.GetFileName(psdRelPath)} — PNG 인코딩 불가 (압축 포맷 해제 실패?)");
                errorCount++;
                continue;
            }

            // ▼ 핵심: PNG 파일 + meta 저장 → PSD + meta 삭제
            // AssetDatabase.DeleteAsset 대신 System.IO 사용 → GUID 레코드 유지
            File.WriteAllBytes(pngAbsPath, pngBytes);
            File.WriteAllText(pngAbsPath + ".meta", metaContent); // 동일 GUID 이식
            File.Delete(psdAbsPath);
            File.Delete(metaAbsPath);
            // ▲ Refresh 시 Unity가 PNG를 기존 GUID로 인식 → 모든 참조 자동 복원

            // 로그 메시지 구성 (리사이즈 여부 포함)
            bool wasResized = psdResizeOver2048 && entry.NeedsResize(2048);
            string resizeNote = wasResized ? $" [리사이즈: {entry.DimLabel} → {tex.width}×{tex.height}]" : "";
            successCount++;
            psdResultLog.Add($"✅ {Path.GetFileName(psdRelPath)} → .png{resizeNote}  [GUID: {guid}]");
            Debug.Log($"[PSD→PNG] 변환 완료: {psdRelPath}{resizeNote} (GUID: {guid})");
        }

        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();

        psdFoundEntries.Clear();
        psdScanned = false;

        string summary = $"변환 성공: {successCount}개  /  스킵: {skipCount}개  /  실패: {errorCount}개";
        psdResultLog.Add("");
        psdResultLog.Add("— " + summary);
        EditorUtility.DisplayDialog("변환 완료", summary, "확인");
        Debug.Log($"[PSD→PNG] {summary}");
        Repaint();
    }

    // 변환 전 일괄 임포터 설정
    // ① Read/Write 활성화
    // ② 압축 포맷 → Uncompressed 강제 (EncodeToPNG는 압축 포맷 미지원)
    // ③ psdResizeOver2048 옵션 시 maxTextureSize = 2048 설정
    void PrepareImporters()
    {
        foreach (var entry in psdFoundEntries)
        {
            if (!entry.include) continue;

            var importer = AssetImporter.GetAtPath(entry.relPath) as TextureImporter;
            if (importer == null) continue;

            bool changed = false;

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                changed = true;
            }

            // 기본 압축 설정 해제
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                changed = true;
            }

            // 플랫폼별 압축 오버라이드가 있으면 해제
            // (플랫폼 override는 textureCompression보다 우선 적용되어 DXT 등이 남아있을 수 있음)
            string[] platforms = { "Standalone", "Android", "iPhone", "WebGL" };
            foreach (var platform in platforms)
            {
                var ps = importer.GetPlatformTextureSettings(platform);
                if (ps.overridden)
                {
                    ps.overridden = false;
                    importer.SetPlatformTextureSettings(ps);
                    changed = true;
                }
            }

            // 원본 해상도 전체로 로드될 수 있도록 maxTextureSize 보장
            int maxSrcDim = Mathf.Max(entry.srcWidth, entry.srcHeight);
            if (!psdResizeOver2048 && importer.maxTextureSize < maxSrcDim)
            {
                importer.maxTextureSize = 8192;
                changed = true;
            }

            // 원본이 2048 초과이고 리사이즈 옵션이 켜져있으면 maxTextureSize 제한
            if (psdResizeOver2048 && entry.NeedsResize(2048) && importer.maxTextureSize > 2048)
            {
                importer.maxTextureSize = 2048;
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();
        }
        AssetDatabase.Refresh();
    }

    // =====================================================
    // 섹션 3 메서드: 텍스처 리사이즈
    // =====================================================

    // 2048 초과 텍스처 탐색
    void ScanLargeTextures()
    {
        texResizeEntries.Clear();
        texResizeLog.Clear();
        texResizeScanned = false;

        string absSearchPath = ProjectRelativeToAbsolute(texResizePath);
        if (!Directory.Exists(absSearchPath))
        {
            EditorUtility.DisplayDialog("오류", $"경로를 찾을 수 없습니다:\n{texResizePath}", "확인");
            return;
        }

        // 전체 파일을 가져온 뒤 확장자로 필터링 (대소문자 무관)
        string[] allFiles = Directory.GetFiles(absSearchPath, "*.*", SearchOption.AllDirectories);
        var extSet = new HashSet<string>(ResizableTexExts);

        foreach (string absPath in allFiles)
        {
            string ext = Path.GetExtension(absPath).ToLower();
            if (!extSet.Contains(ext)) continue;

            string relPath = GetProjectRelativePath(absPath);
            if (string.IsNullOrEmpty(relPath)) continue;

            var importer = AssetImporter.GetAtPath(relPath) as TextureImporter;
            if (importer == null) continue;

            importer.GetSourceTextureWidthAndHeight(out int srcW, out int srcH);
            if (srcW <= 0 || srcH <= 0) continue;
            if (srcW <= 2048 && srcH <= 2048) continue; // 이미 2048 이하이면 제외

            // 목표 해상도 계산 (비율 유지, 긴 변이 2048)
            float scale = Mathf.Min(2048f / srcW, 2048f / srcH);
            int dstW = Mathf.RoundToInt(srcW * scale);
            int dstH = Mathf.RoundToInt(srcH * scale);

            // 스캔 시점에 원본 meta 내용 저장
            // (PrepareTexResizeImporters가 임포터를 수정하기 전에 읽어야 함)
            string metaContent = string.Empty;
            string metaAbsPath = absPath + ".meta";
            if (File.Exists(metaAbsPath))
                metaContent = File.ReadAllText(metaAbsPath);

            // NormalMap은 isReadable + Uncompressed 설정 시 채널이 재배치되어 색상 왜곡 발생
            // Default 타입으로 임시 변경 후 원본 픽셀 그대로 로드하기 위해 플래그 저장
            bool isNormalMap = importer.textureType == TextureImporterType.NormalMap;
            bool isLinearSource = isNormalMap || !importer.sRGBTexture;

            texResizeEntries.Add(new TexEntry
            {
                relPath             = relPath,
                srcWidth            = srcW,
                srcHeight           = srcH,
                outputExt           = GetOutputExtension(ext),
                targetWidth         = dstW,
                targetHeight        = dstH,
                originalMetaContent = metaContent,
                isLinearSource      = isLinearSource
            });
        }

        texResizeScanned = true;
        int formatChangeCount = 0;
        foreach (var e in texResizeEntries) if (e.IsFormatChange) formatChangeCount++;
        Debug.Log($"[텍스처 리사이즈] 스캔 완료 — {texResizeEntries.Count}개 발견 / 포맷 변환: {formatChangeCount}개 ({texResizePath})");
        Repaint();
    }

    // 일괄 리사이즈 실행
    void ResizeAllTextures()
    {
        texResizeLog.Clear();
        int successCount = 0, skipCount = 0, errorCount = 0;

        // 임포터 준비: Read/Write + Uncompressed + maxTextureSize를 원본 이상으로 확장
        PrepareTexResizeImporters();

        var targets = new List<TexEntry>();
        foreach (var e in texResizeEntries) if (e.include) targets.Add(e);

        for (int i = 0; i < targets.Count; i++)
        {
            var entry = targets[i];
            string relPath   = entry.relPath;
            string absPath   = ProjectRelativeToAbsolute(relPath);
            string inputExt  = Path.GetExtension(relPath).ToLower();
            string outputRelPath = Path.ChangeExtension(relPath, entry.outputExt).Replace('\\', '/');
            string outputAbsPath = ProjectRelativeToAbsolute(outputRelPath);

            EditorUtility.DisplayProgressBar(
                "텍스처 리사이즈 중",
                Path.GetFileName(relPath),
                (float)i / targets.Count);

            // 텍스처 로드
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
            if (tex == null)
            {
                Debug.LogWarning($"[텍스처 리사이즈] 텍스처 로드 실패: {relPath}");
                texResizeLog.Add($"❌ 실패: {Path.GetFileName(relPath)} — 텍스처 로드 불가");
                errorCount++;
                continue;
            }

            // 스캔 시점에 기록한 플래그 사용
            // (PrepareTexResizeImporters 이후에는 importer 설정이 바뀌어 있으므로 재조회 금지)
            bool isLinear = entry.isLinearSource;

            // RenderTexture를 이용한 고품질 리사이즈
            var resized = ResizeTexture(tex, entry.targetWidth, entry.targetHeight, isLinear);
            if (resized == null)
            {
                texResizeLog.Add($"❌ 실패: {Path.GetFileName(relPath)} — 리사이즈 실패");
                errorCount++;
                continue;
            }

            // 출력 포맷에 맞게 인코딩
            byte[] bytes = EncodeTexture(resized, entry.outputExt);
            DestroyImmediate(resized);

            if (bytes == null || bytes.Length == 0)
            {
                texResizeLog.Add($"❌ 실패: {Path.GetFileName(relPath)} — 인코딩 실패");
                errorCount++;
                continue;
            }

            if (string.IsNullOrEmpty(entry.originalMetaContent))
            {
                texResizeLog.Add($"⚠️ 스킵: {Path.GetFileName(relPath)} — .meta 없음");
                skipCount++;
                continue;
            }

            if (entry.IsFormatChange)
            {
                // ▼ 포맷 변환: 새 확장자로 저장 + 원본 meta 이식(GUID 보존) + 원본 삭제
                File.WriteAllBytes(outputAbsPath, bytes);
                // PrepareTexResizeImporters가 meta를 수정했을 수 있으므로 스캔 시 저장한 원본 내용 사용
                File.WriteAllText(outputAbsPath + ".meta", entry.originalMetaContent);
                File.Delete(absPath);
                File.Delete(absPath + ".meta");
                // ▲ Refresh 시 Unity가 새 파일을 기존 GUID + 원본 설정(NormalMap 등)으로 reimport
            }
            else
            {
                // 동일 포맷: 파일 덮어쓰기 후 원본 meta 복원
                File.WriteAllBytes(absPath, bytes);
                // PrepareTexResizeImporters가 textureType 등을 변경했으므로 원본 meta 복원
                File.WriteAllText(absPath + ".meta", entry.originalMetaContent);
                // ▲ Refresh 시 NormalMap, sRGB 등 원본 임포터 설정 그대로 reimport
            }

            successCount++;
            string formatNote = entry.IsFormatChange ? $" [{inputExt}→{entry.outputExt}]" : "";
            texResizeLog.Add($"✅ {Path.GetFileName(relPath)}{formatNote}  {entry.SrcDimLabel} → {entry.DstDimLabel}");
            Debug.Log($"[텍스처 리사이즈] 완료: {relPath}{formatNote}  {entry.SrcDimLabel} → {entry.DstDimLabel}");
        }

        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();

        texResizeEntries.Clear();
        texResizeScanned = false;

        string summary = $"리사이즈 성공: {successCount}개  /  스킵: {skipCount}개  /  실패: {errorCount}개";
        texResizeLog.Add("");
        texResizeLog.Add("— " + summary);
        EditorUtility.DisplayDialog("리사이즈 완료", summary, "확인");
        Debug.Log($"[텍스처 리사이즈] {summary}");
        Repaint();
    }

    // 리사이즈 전 일괄 임포터 설정
    // ① Read/Write 활성화  ② Uncompressed 강제  ③ maxTextureSize를 원본 크기 이상으로 확보
    void PrepareTexResizeImporters()
    {
        foreach (var entry in texResizeEntries)
        {
            if (!entry.include) continue;

            var importer = AssetImporter.GetAtPath(entry.relPath) as TextureImporter;
            if (importer == null) continue;

            bool changed = false;

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                changed = true;
            }

            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                changed = true;
            }

            // ▼ 핵심: NormalMap 타입은 임시로 Default로 변경
            // NormalMap 상태에서 Uncompressed 로드 시 Unity가 채널을 재배치(X→Alpha, Y→Green)해
            // 픽셀 데이터가 왜곡됨. Default 타입으로 바꾸면 원본 RGB 픽셀 그대로 읽을 수 있음.
            // 저장 후 originalMetaContent를 복원하면 NormalMap 설정이 자동 복구됨.
            if (importer.textureType == TextureImporterType.NormalMap)
            {
                importer.textureType = TextureImporterType.Default;
                // textureType 변경 시 Unity가 sRGBTexture를 암묵적으로 true로 바꿀 수 있음
                // 노말맵 데이터는 Linear이므로 반드시 명시적으로 false 지정
                // (sRGB=true 상태로 로드되면 sRGB→Linear 변환이 끼어들어 채널값이 왜곡됨)
                importer.sRGBTexture = false;
                changed = true;
            }

            // 원본 전체 해상도로 로드할 수 있도록 maxTextureSize를 원본보다 크게 설정
            int maxSrcDim = Mathf.Max(entry.srcWidth, entry.srcHeight);
            if (importer.maxTextureSize < maxSrcDim)
            {
                importer.maxTextureSize = 8192; // 8192면 모든 실용적인 케이스 커버
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();
        }
        AssetDatabase.Refresh();
    }

    // RenderTexture를 이용한 고품질 바이리니어 리사이즈
    // isLinear: 노말맵·마스크맵 등 sRGB 변환이 없어야 하는 Linear 텍스처면 true
    Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight, bool isLinear = false)
    {
        // HDR/float 포맷 여부 판단 (EXR, HDR 텍스처)
        bool isHdr = source.format == TextureFormat.RGBAFloat ||
                     source.format == TextureFormat.RGBAHalf  ||
                     source.format == TextureFormat.RHalf      ||
                     source.format == TextureFormat.RFloat;

        var rtFormat  = isHdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32;
        var texFormat = isHdr ? TextureFormat.RGBAFloat        : TextureFormat.RGBA32;

        // Linear 텍스처(노말맵, 마스크맵 등) 또는 HDR은 색공간 변환 없이 처리
        // Default를 쓰면 Blit 과정에서 sRGB 변환이 끼어들어 데이터가 왜곡됨
        var readWrite = (isLinear || isHdr)
            ? RenderTextureReadWrite.Linear
            : RenderTextureReadWrite.Default;

        var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, rtFormat, readWrite);
        rt.filterMode = FilterMode.Bilinear;
        RenderTexture.active = rt;
        Graphics.Blit(source, rt);

        // Texture2D 생성 시에도 linear 플래그를 일치시켜야 ReadPixels 결과가 정확함
        var result = new Texture2D(targetWidth, targetHeight, texFormat, false, isLinear || isHdr);
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();

        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    // 출력 확장자에 맞는 인코딩
    byte[] EncodeTexture(Texture2D tex, string ext)
    {
        switch (ext.ToLower())
        {
            case ".jpg":
            case ".jpeg":
                return tex.EncodeToJPG(texJpgQuality);
            case ".exr":
            case ".hdr":  // HDR → EXR (float 정보 보존)
                return tex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
            case ".tga":
                return tex.EncodeToTGA();
            default:      // .png, .bmp, .tif, .tiff, .gif → PNG
                return tex.EncodeToPNG();
        }
    }

    // 입력 확장자에 따른 출력 확장자 결정
    // Unity 인코더 미지원 포맷은 PNG(일반) 또는 EXR(HDR)로 변환
    static string GetOutputExtension(string inputExt)
    {
        switch (inputExt.ToLower())
        {
            case ".jpg":  case ".jpeg": return inputExt.ToLower();
            case ".exr":               return ".exr";
            case ".hdr":               return ".exr"; // HDR → EXR (float 보존)
            case ".tga":               return ".tga";
            default:                   return ".png"; // BMP, TIF, TIFF, GIF → PNG
        }
    }

    // =====================================================
    // 공통 유틸리티
    // =====================================================

	string ChooseFolder(string currentProjectRelativePath)
	{
		string initialAbsolute = string.Empty;
		if (!string.IsNullOrEmpty(currentProjectRelativePath) && currentProjectRelativePath.StartsWith("Assets"))
		{
			string assetsAbsolute = Application.dataPath; // .../Project/Assets
			string sub = currentProjectRelativePath.Length > 6 ? currentProjectRelativePath.Substring(6).TrimStart('/', '\\') : string.Empty;
			initialAbsolute = string.IsNullOrEmpty(sub) ? assetsAbsolute : Path.Combine(assetsAbsolute, sub);
		}

		string selectedAbsolute = EditorUtility.OpenFolderPanel("폴더 선택", string.IsNullOrEmpty(initialAbsolute) ? Application.dataPath : initialAbsolute, "");
		if (string.IsNullOrEmpty(selectedAbsolute)) return null;

		string projectRelative = GetProjectRelativePath(selectedAbsolute);
		if (string.IsNullOrEmpty(projectRelative))
		{
			EditorUtility.DisplayDialog("경고", "프로젝트 폴더(Assets) 내부의 폴더만 선택할 수 있습니다.", "확인");
			return null;
		}
		return projectRelative;
	}

    // 절대 경로 → project-relative 경로 (Assets/...)
	string GetProjectRelativePath(string absolutePath)
	{
		if (string.IsNullOrEmpty(absolutePath)) return null;
		string assetsAbsolute = Application.dataPath.Replace('\\', '/');
		string normalized = absolutePath.Replace('\\', '/');
		if (!normalized.StartsWith(assetsAbsolute)) return null;
		string sub = normalized.Substring(assetsAbsolute.Length).TrimStart('/');
		return string.IsNullOrEmpty(sub) ? "Assets" : "Assets/" + sub;
	}

    // project-relative 경로 (Assets/...) → 절대 경로
    string ProjectRelativeToAbsolute(string projectRelativePath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        if (projectRelativePath == "Assets") return dataPath;
        if (projectRelativePath.StartsWith("Assets/"))
            return dataPath + "/" + projectRelativePath.Substring("Assets/".Length);
        return projectRelativePath;
    }

    // 섹션 구분 헤더 (가로선 + 폴드아웃) — 새 foldout 상태를 반환
    bool DrawFoldoutSectionHeader(bool foldout, string title)
    {
        GUILayout.Space(2);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        return EditorGUILayout.Foldout(foldout, title, true, EditorStyles.foldoutHeader);
    }

    GUIStyle GetRedStyle()
    {
        var style = new GUIStyle(EditorStyles.label);
        style.normal.textColor = Color.red;
        return style;
    }

    GUIStyle GetWarningStyle()
    {
        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = new Color(1f, 0.6f, 0f); // 주황색
        return style;
    }

    GUIStyle GetGrayStyle()
    {
        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = Color.gray;
        return style;
    }

    GUIStyle GetBlueStyle()
    {
        var style = new GUIStyle(EditorStyles.miniLabel);
        style.normal.textColor = new Color(0.4f, 0.7f, 1f); // 하늘색
        return style;
    }
}
