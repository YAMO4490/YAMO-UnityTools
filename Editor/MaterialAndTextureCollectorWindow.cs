using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public class MaterialAndTextureTool : EditorWindow
{
    private GameObject targetPrefab;
    private string materialOutputPath = "Assets/DuplicatedMaterials";
    private string textureOutputPath = "Assets/DuplicatedTextures";

    private Dictionary<string, List<Material>> duplicateMaterialMap = new Dictionary<string, List<Material>>();
    private Dictionary<string, List<Texture>> duplicateTextureMap = new Dictionary<string, List<Texture>>();
    private Dictionary<Material, Material> materialCopies = new Dictionary<Material, Material>();
    private Dictionary<Texture, Texture> textureCopies = new Dictionary<Texture, Texture>();
    private HashSet<Material> collectedMaterials = new HashSet<Material>();
    private HashSet<Texture> collectedTextures = new HashSet<Texture>();
    private Vector2 scroll;

    [MenuItem("Tools/Material And Texture Tool")]
    public static void ShowWindow()
    {
        // 변경된 부분: 이미 창이 열려있는지 확인하여 토글(Toggle) 기능 구현
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
        materialOutputPath = EditorGUILayout.TextField("\uD83D\uDCC2 머테리얼 저장 경로", materialOutputPath);
        textureOutputPath = EditorGUILayout.TextField("\uD83D\uDCC2 텍스처 저장 경로", textureOutputPath);

        if (GUILayout.Button("\uD83D\uDCCB 중복 이름 검사")) CollectDuplicates();
        if (GUILayout.Button("\uD83D\uDD04 중복 이름 자동 변경")) RenameDuplicateAssets();
        if (GUILayout.Button("\uD83D\uDD04 머테리얼 및 텍스처 복사")) DuplicateMaterialsAndTextures();

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
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    string propName = shader.GetPropertyName(i);
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
        Dictionary<string, int> renameCount = new Dictionary<string, int>();

        RenameAssetGroup(duplicateMaterialMap, ".mat", renameCount);
        RenameAssetGroup(duplicateTextureMap, null, renameCount);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    void RenameAssetGroup<T>(Dictionary<string, List<T>> map, string extOverride, Dictionary<string, int> counter) where T : Object
    {
        foreach (var pair in map)
        {
            if (pair.Value.Count < 2) continue;
            foreach (var obj in pair.Value)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;

                if (!counter.ContainsKey(pair.Key)) counter[pair.Key] = 1;
                else counter[pair.Key] += 1;

                string newName = pair.Key + "_" + counter[pair.Key];
                string result = AssetDatabase.RenameAsset(path, newName);
                if (result != "")
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

        if (!AssetDatabase.IsValidFolder(materialOutputPath)) AssetDatabase.CreateFolder("Assets", "DuplicatedMaterials");
        if (!AssetDatabase.IsValidFolder(textureOutputPath)) AssetDatabase.CreateFolder("Assets", "DuplicatedTextures");

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
        int count = shader.GetPropertyCount();

        string[] maskProps = { "_MaskMap", "_OcclusionMap", "_DetailMask", "_RoughnessMap", "_MetallicGlossMap" };

        for (int i = 0; i < count; i++)
        {
            string prop = shader.GetPropertyName(i);
            Texture tex = original.GetTexture(prop);
            if (tex == null) continue;

            bool isTexEnv = shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture;
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

    GUIStyle GetRedStyle()
    {
        var style = new GUIStyle(EditorStyles.label);
        style.normal.textColor = Color.red;
        return style;
    }
}