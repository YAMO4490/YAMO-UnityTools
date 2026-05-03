using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Originally from VRCDeveloperTool by gatosyocora (MIT License)
// https://github.com/gatosyocora/VRCDeveloperTool
// Refactored for YAMO Unity Tools.
// 원본의 Resources/ShapeKeyNameChanger/shapekeynames.json 데이터는
// 패키지 의존성을 줄이기 위해 본 파일 내 상수로 인라인됨 (MMD 표준 셰이프키 이름).

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// SkinnedMeshRenderer 메시의 BlendShape(셰이프키) 이름을 변경한
    /// 새 메시(*_custom.asset)를 생성하는 에디터 윈도우.
    /// 직접 입력 / 사전 정의 목록(MMD 표준)에서 선택의 두 가지 모드 지원.
    /// 일괄 변환: 첫 글자 대/소문자, _L/_R → Left/Right.
    /// </summary>
    public class ShapeKeyNameChanger : EditorWindow
    {
        private List<string> shapeKeyNames;
        private SkinnedMeshRenderer renderer;
        private string[] posNames;
        private bool useDuplication = false;
        private Vector2 scrollPos = Vector2.zero;

        enum SelectType
        {
            Input,
            Select
        }

        private SelectType selectTab = SelectType.Input;

        private static GUIContent[] tabToggles
        {
            get { return System.Enum.GetNames(typeof(SelectType)).Select(x => new GUIContent(x)).ToArray(); }
        }

        private string[] selectableNames;
        private int[] selectedIndices;

        // 원본 shapekeynames.json (MMD 표준 셰이프키 이름) 인라인
        private static readonly string[] DefaultSelectableNames = new[]
        {
            "まばたき", "笑い", "ウィンク", "ウィンク右", "ウィンク２", "ｳｨﾝｸ２右",
            "はぅ", "なごみ", "びっくり", "じと目", "星目", "白目", "はぁと", "照れ",
            "瞳小", "瞳大", "ｷﾘｯ", "ワ", "あ", "い", "う", "お", "∧", "ω", "ω□", "▲",
            "えー", "にっこり", "口角上げ", "口角下げ", "口角広げ", "歯無し上", "歯無し下",
            "てへぺろ２", "上", "下", "真面目", "怒り", "困る", "にこり", "青ざめる",
            "ハイライト消し", "光下", "ぺろっ", "涙"
        };

        [MenuItem("Tools/YAMO/Mesh/ShapeKey Name Changer")]
        private static void Create()
        {
            GetWindow<ShapeKeyNameChanger>("ShapeKey Name Changer");
        }

        private void OnEnable()
        {
            shapeKeyNames = null;
            renderer = null;
            posNames = null;
            selectableNames = DefaultSelectableNames;
        }

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                renderer = EditorGUILayout.ObjectField(
                    "Renderer",
                    renderer,
                    typeof(SkinnedMeshRenderer),
                    true
                ) as SkinnedMeshRenderer;

                if (check.changed)
                {
                    if (renderer == null)
                    {
                        shapeKeyNames = null;
                        posNames = null;
                        selectedIndices = null;
                    }
                    else
                    {
                        shapeKeyNames = GetBlendShapeListFromRenderer(renderer);
                        posNames = shapeKeyNames.ToArray();
                        selectedIndices = new int[shapeKeyNames.Count];
                        for (int i = 0; i < selectedIndices.Length; i++)
                        {
                            selectedIndices[i] = -1;
                        }
                    }
                }
            }

            selectTab = (SelectType)GUILayout.Toolbar((int)selectTab, tabToggles, "LargeButton", GUI.ToolbarButtonSize.Fixed);

            if (shapeKeyNames != null)
            {
                using (var pos = new GUILayout.ScrollViewScope(scrollPos))
                {
                    scrollPos = pos.scrollPosition;

                    if (selectTab == SelectType.Input)
                    {
                        for (int i = 0; i < shapeKeyNames.Count; i++)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                using (var toggle = new EditorGUI.ChangeCheckScope())
                                {
                                    EditorGUILayout.Toggle(shapeKeyNames[i] != posNames[i], GUILayout.Width(30));
                                    if (toggle.changed && shapeKeyNames[i] != posNames[i])
                                    {
                                        posNames[i] = shapeKeyNames[i];
                                        selectedIndices[i] = -1;
                                    }
                                }
                                posNames[i] = EditorGUILayout.TextField(shapeKeyNames[i], posNames[i]);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < shapeKeyNames.Count; i++)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                using (var toggle = new EditorGUI.ChangeCheckScope())
                                {
                                    EditorGUILayout.Toggle(shapeKeyNames[i] != posNames[i], GUILayout.Width(30));
                                    if (toggle.changed && shapeKeyNames[i] != posNames[i])
                                    {
                                        posNames[i] = shapeKeyNames[i];
                                        selectedIndices[i] = -1;
                                    }
                                }

                                using (var check = new EditorGUI.ChangeCheckScope())
                                {
                                    selectedIndices[i] = EditorGUILayout.Popup(shapeKeyNames[i], selectedIndices[i], selectableNames);

                                    if (check.changed && selectedIndices[i] != -1)
                                        posNames[i] = selectableNames[selectedIndices[i]];
                                }
                            }
                        }
                    }
                }
            }

            using (new EditorGUI.DisabledScope(renderer == null))
            {
                useDuplication = EditorGUILayout.Toggle("Duplication ShapeKeys", useDuplication);

                if (GUILayout.Button("Convert First Letter to Lowercase"))
                {
                    ConvertFirstLetterToLowercase();
                }

                if (GUILayout.Button("Convert First Letter to Uppercase"))
                {
                    ConvertFirstLetterToUppercase();
                }

                if (GUILayout.Button("Change ShapeKeyName"))
                {
                    CreateNewShapeKeyNameMesh(renderer, posNames, useDuplication, shapeKeyNames);

                    shapeKeyNames = GetBlendShapeListFromRenderer(renderer);
                    posNames = shapeKeyNames.ToArray();
                    selectedIndices = new int[shapeKeyNames.Count];
                    for (int i = 0; i < selectedIndices.Length; i++)
                    {
                        selectedIndices[i] = -1;
                    }
                }

                if (GUILayout.Button("Convert _L/_R to Left/Right"))
                {
                    ConvertLRToFullName();
                }
            }
        }

        private void ConvertFirstLetterToLowercase()
        {
            for (int i = 0; i < posNames.Length; i++)
            {
                if (string.IsNullOrEmpty(posNames[i])) continue;
                if (!char.IsLower(posNames[i][0]))
                {
                    posNames[i] = char.ToLower(posNames[i][0]) + posNames[i].Substring(1);
                }
            }
        }

        private void ConvertFirstLetterToUppercase()
        {
            for (int i = 0; i < posNames.Length; i++)
            {
                if (string.IsNullOrEmpty(posNames[i])) continue;
                if (!char.IsUpper(posNames[i][0]))
                {
                    posNames[i] = char.ToUpper(posNames[i][0]) + posNames[i].Substring(1);
                }
            }
        }

        private void ConvertLRToFullName()
        {
            for (int i = 0; i < posNames.Length; i++)
            {
                if (posNames[i].EndsWith("_L"))
                {
                    posNames[i] = posNames[i].Replace("_L", "Left");
                }
                else if (posNames[i].EndsWith("_R"))
                {
                    posNames[i] = posNames[i].Replace("_R", "Right");
                }
            }
        }

        private bool CreateNewShapeKeyNameMesh(SkinnedMeshRenderer renderer, string[] posShapeKeyNames, bool useDuplication, List<string> preShapeKeyNames)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null) return false;
            if (posShapeKeyNames.Length != mesh.blendShapeCount) return false;

            var meshCustom = Object.Instantiate(mesh);
            meshCustom.ClearBlendShapes();

            int frameIndex = 0;
            for (int blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
            {
                var deltaVertices = new Vector3[mesh.vertexCount];
                var deltaNormals = new Vector3[mesh.vertexCount];
                var deltaTangents = new Vector3[mesh.vertexCount];

                mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                float weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);
                string shapeKeyName = posShapeKeyNames[blendShapeIndex];

                if (useDuplication && !preShapeKeyNames[blendShapeIndex].Equals(shapeKeyName))
                {
                    meshCustom.AddBlendShapeFrame(preShapeKeyNames[blendShapeIndex], weight, deltaVertices, deltaNormals, deltaTangents);
                }

                meshCustom.AddBlendShapeFrame(shapeKeyName, weight, deltaVertices, deltaNormals, deltaTangents);
            }

            Undo.RecordObject(renderer, "Renderer " + renderer.name);
            renderer.sharedMesh = meshCustom;

            var path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh)) + "/" + mesh.name + "_custom.asset";
            AssetDatabase.CreateAsset(meshCustom, AssetDatabase.GenerateUniqueAssetPath(path));
            AssetDatabase.SaveAssets();

            return true;
        }

        private List<string> GetBlendShapeListFromRenderer(SkinnedMeshRenderer renderer)
        {
            var names = new List<string>();
            var mesh = renderer.sharedMesh;

            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    names.Add(mesh.GetBlendShapeName(i));

            return names;
        }
    }
}
