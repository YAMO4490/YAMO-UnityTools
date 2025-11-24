using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public class MaterialAndTextureTool : EditorWindow
{
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
    private Vector2 scroll;

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
            window.minSize = new Vector2(600, 500);
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("\uD83C\uDF1F 머테리얼 & 텍스처 유틸리티", EditorStyles.boldLabel);

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

		if (GUILayout.Button("\uD83D\uDCCB 중복 이름 검사")) CollectDuplicates();
        if (GUILayout.Button("\uD83D\uDD04 중복 이름 자동 변경")) RenameDuplicateAssets();
        if (GUILayout.Button("\uD83D\uDD04 머테리얼 및 텍스처 복사")) DuplicateMaterialsAndTextures();
		if (GUILayout.Button("\uD83D\uDCE6 중복 없으면 참조 자산 이동")) MoveReferencedAssetsIfNoDuplicates();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        GUILayout.Space(10);
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
            {
                EditorGUIUtility.PingObject(mat);
            }
            EditorGUILayout.ObjectField(mat, typeof(Material), false);
            EditorGUILayout.EndHorizontal();
        }

        GUILayout.Space(10);
        GUILayout.Label("\uD83D\uDD0D 참조된 모든 텍스처", EditorStyles.boldLabel);
        foreach (var tex in collectedTextures)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(tex.name, GUILayout.Width(200)))
            {
                EditorGUIUtility.PingObject(tex);
            }
            EditorGUILayout.ObjectField(tex, typeof(Texture), false);
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

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

	string GetProjectRelativePath(string absolutePath)
	{
		if (string.IsNullOrEmpty(absolutePath)) return null;
		string assetsAbsolute = Application.dataPath.Replace('\\', '/');
		string normalized = absolutePath.Replace('\\', '/');
		if (!normalized.StartsWith(assetsAbsolute)) return null;
		string sub = normalized.Substring(assetsAbsolute.Length).TrimStart('/');
		return string.IsNullOrEmpty(sub) ? "Assets" : "Assets/" + sub;
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

    GUIStyle GetRedStyle()
    {
        var style = new GUIStyle(EditorStyles.label);
        style.normal.textColor = Color.red;
        return style;
    }
}