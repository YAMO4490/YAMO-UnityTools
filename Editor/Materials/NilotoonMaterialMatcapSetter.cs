using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using System.Collections.Generic;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 매트캡 정보를 담는 구조체. lilToon의 MatCap 1/2 슬롯에서 추출한 값을
    /// NiloToon의 BaseMapStackingLayer 슬롯으로 옮길 때 중간 저장소 역할.
    /// </summary>
    public struct stMatcapInfo
    {
        public bool _UseMatCap;
        public Texture _MatCapTex;
        public Color _MatCapColor;
        public Texture _MatCapBlendMask;
        public int _MatCapBlendMode;

        public stMatcapInfo(bool _useMatCap)
        {
            _UseMatCap = _useMatCap;
            _MatCapTex = null;
            _MatCapColor = Color.white;
            _MatCapBlendMask = null;
            _MatCapBlendMode = 0;
        }

        public stMatcapInfo(bool _useMatCap, Texture _matCapTex, Color _matCapColor,
            Texture _matCapBlendMask, int _matCapBlendMode)
        {
            _UseMatCap = _useMatCap;
            _MatCapTex = _matCapTex;
            _MatCapColor = _matCapColor;
            _MatCapBlendMask = _matCapBlendMask;
            _MatCapBlendMode = _matCapBlendMode;
        }
    }

    /// <summary>
    /// NiloToon 머티리얼에 드래그된 lilToon 머티리얼의 MatCap 설정을 자동 복사.
    /// - 원본 셰이더를 lilToon으로 임시 교체해 MatCap 프로퍼티를 읽고,
    ///   결과를 원본(NiloToon)의 BaseMapStackingLayer[n] 슬롯에 주입.
    /// - lilToon 셰이더가 프로젝트에 없으면 에러 로그 후 중단.
    /// </summary>
    public class NilotoonMaterialMatcapSetter : EditorWindow
    {
        // 외부 프로젝트에 있을 수 있는 공용 스타일시트 (없으면 조용히 무시).
        private const string CommonUssPath = "Assets/Scripts/Streamingle/StreamingleControl/Editor/UXML/StreamingleCommon.uss";
        private const string targetShaderName = "lilToon";

        private List<Material> materials = new List<Material>();
        private string pEnableName_Front = "_BaseMapStackingLayer";
        private string pEnableName_Back = "Enable";

        private VisualElement materialListContainer;
        private VisualElement dropArea;

        [MenuItem("Tools/YAMO/Materials/닐로툰 매트캡 자동 인식기")]
        public static void ShowWindow()
        {
            GetWindow<NilotoonMaterialMatcapSetter>("닐로툰 매트캡 자동 인식기");
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList("tool-root");

            var commonUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(CommonUssPath);
            if (commonUss != null) root.styleSheets.Add(commonUss);

            root.Add(new Label("닐로툰 매트캡 자동 인식기") { name = "title" });
            root.Q<Label>("title").AddToClassList("tool-title");

            root.Add(new HelpBox("선택한 닐로툰에서 릴툰 매트캡 값을 찾아 닐로툰에 자동 반영합니다.", HelpBoxMessageType.Info));

            // 드래그 앤 드롭 영역
            root.Add(new Label("수정할 머티리얼들:") { style = { marginTop = 8, unityFontStyleAndWeight = FontStyle.Bold } });

            dropArea = new VisualElement();
            dropArea.style.height = 50;
            dropArea.style.backgroundColor = new Color(0, 0, 0, 0.2f);
            dropArea.style.borderTopWidth = dropArea.style.borderRightWidth =
                dropArea.style.borderBottomWidth = dropArea.style.borderLeftWidth = 2;
            dropArea.style.borderTopColor = dropArea.style.borderRightColor =
                dropArea.style.borderBottomColor = dropArea.style.borderLeftColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            dropArea.style.borderTopLeftRadius = dropArea.style.borderTopRightRadius =
                dropArea.style.borderBottomLeftRadius = dropArea.style.borderBottomRightRadius = 4;
            dropArea.style.justifyContent = Justify.Center;
            dropArea.style.alignItems = Align.Center;
            dropArea.Add(new Label("여기에 머티리얼을 드래그하세요") { style = { color = new Color(0.6f, 0.6f, 0.6f) } });

            dropArea.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                dropArea.style.borderTopColor = dropArea.style.borderRightColor =
                    dropArea.style.borderBottomColor = dropArea.style.borderLeftColor = new Color(0.4f, 0.6f, 1f);
                evt.StopPropagation();
            });
            dropArea.RegisterCallback<DragLeaveEvent>(evt =>
            {
                dropArea.style.borderTopColor = dropArea.style.borderRightColor =
                    dropArea.style.borderBottomColor = dropArea.style.borderLeftColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            });
            dropArea.RegisterCallback<DragPerformEvent>(evt =>
            {
                DragAndDrop.AcceptDrag();
                foreach (Object draggedObject in DragAndDrop.objectReferences)
                {
                    if (draggedObject is Material mat)
                        materials.Add(mat);
                }
                dropArea.style.borderTopColor = dropArea.style.borderRightColor =
                    dropArea.style.borderBottomColor = dropArea.style.borderLeftColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                RebuildMaterialList();
                evt.StopPropagation();
            });
            root.Add(dropArea);

            // 추가된 머티리얼 목록
            root.Add(new Label("추가된 머티리얼:") { style = { marginTop = 8, unityFontStyleAndWeight = FontStyle.Bold } });
            var materialScroll = new ScrollView { style = { maxHeight = 100 } };
            materialListContainer = new VisualElement();
            materialScroll.Add(materialListContainer);
            root.Add(materialScroll);

            // 실행 버튼
            var processBtn = new Button(() =>
            {
                ProcessMaterials();
                materials.Clear();
                RebuildMaterialList();
            }) { text = "자동 복사 실행" };
            processBtn.style.height = 30;
            processBtn.style.marginTop = 8;
            processBtn.AddToClassList("btn-primary");
            root.Add(processBtn);
        }

        private void RebuildMaterialList()
        {
            if (materialListContainer == null) return;
            materialListContainer.Clear();

            if (materials.Count == 0)
            {
                materialListContainer.Add(new Label("머티리얼 없음") { style = { color = new Color(0.6f, 0.6f, 0.6f), unityFontStyleAndWeight = FontStyle.Italic } });
                return;
            }

            foreach (var mat in materials)
            {
                var objField = new ObjectField { value = mat, objectType = typeof(Material) };
                objField.SetEnabled(false);
                materialListContainer.Add(objField);
            }
        }

        void ProcessMaterials()
        {
            Shader lilToonShader = Shader.Find(targetShaderName);
            if (lilToonShader == null)
            {
                Debug.LogError("lilToon Shader를 찾을 수 없습니다. 먼저 프로젝트에 추가해주세요.");
                return;
            }

            foreach (Material originalMaterial in materials)
            {
                if (originalMaterial == null) continue;

                Material clonedMaterial = Object.Instantiate(originalMaterial);
                clonedMaterial.shader = lilToonShader;

                List<stMatcapInfo> matcapInfoList = new List<stMatcapInfo>();

                stMatcapInfo newInfo1 = GetMatcapInfoByName(clonedMaterial, "_UseMatCap", true);
                stMatcapInfo newInfo2 = GetMatcapInfoByName(clonedMaterial, "_UseMatCap2nd", false);

                if (newInfo1._UseMatCap) matcapInfoList.Add(newInfo1);
                if (newInfo2._UseMatCap) matcapInfoList.Add(newInfo2);

                foreach (stMatcapInfo matcapInfo in matcapInfoList)
                {
                    int targetLayerNumber = 0;
                    for (int i = 1; i <= 10; i++)
                    {
                        if (originalMaterial.GetFloat(pEnableName_Front + i.ToString() + pEnableName_Back) < 0.5f)
                        {
                            targetLayerNumber = i;
                            originalMaterial.SetFloat(pEnableName_Front + i.ToString() + pEnableName_Back, 1f);
                            break;
                        }
                    }

                    string layer = targetLayerNumber.ToString();
                    originalMaterial.SetVector($"_BaseMapStackingLayer{layer}TexUVCenterPivotScalePos", new Vector4(1, 1, 0, 0));
                    originalMaterial.SetVector($"_BaseMapStackingLayer{layer}TexUVScaleOffset", new Vector4(1, 1, 0, 0));
                    originalMaterial.SetVector($"_BaseMapStackingLayer{layer}TexUVAnimSpeed", new Vector4(0, 0, 0, 0));
                    originalMaterial.SetVector($"_BaseMapStackingLayer{layer}MaskTexChannel", new Vector4(0, 1, 0, 0));
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}TexUVRotatedAngle", 0);
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}TexUVRotateSpeed", 0);
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}MaskUVIndex", 0);
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}MaskInvertColor", 0);
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}TexIgnoreAlpha", 0);

                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}ColorBlendMode",
                        matcapInfo._MatCapBlendMode == 0 ? 0 :
                        matcapInfo._MatCapBlendMode == 1 ? 2 :
                        matcapInfo._MatCapBlendMode == 2 ? 3 :
                        matcapInfo._MatCapBlendMode == 3 ? 4 : 5);

                    originalMaterial.SetTexture($"_BaseMapStackingLayer{layer}Tex", matcapInfo._MatCapTex);
                    originalMaterial.SetColor($"_BaseMapStackingLayer{layer}TintColor",
                        new Color(matcapInfo._MatCapColor.r, matcapInfo._MatCapColor.g, matcapInfo._MatCapColor.b, 1f));
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}MasterStrength", matcapInfo._MatCapColor.a);
                    originalMaterial.SetFloat($"_BaseMapStackingLayer{layer}TexUVIndex", 4);
                    originalMaterial.SetTexture($"_BaseMapStackingLayer{layer}MaskTex", matcapInfo._MatCapBlendMask);
                }

                DestroyImmediate(clonedMaterial);
            }

            AssetDatabase.SaveAssets();
        }

        private stMatcapInfo GetMatcapInfoByName(Material mat, string propertyName, bool isFirst)
        {
            string plusName = string.Empty;
            if (!isFirst) plusName = "2nd";

            if (mat.HasProperty(propertyName) && mat.GetInt(propertyName) == 1)
            {
                return new stMatcapInfo(true,
                    mat.GetTexture("_MatCap" + plusName + "Tex"),
                    mat.GetColor("_MatCap" + plusName + "Color"),
                    mat.GetTexture("_MatCap" + plusName + "BlendMask"),
                    mat.GetInt("_MatCap" + plusName + "BlendMode"));
            }
            return new stMatcapInfo(false);
        }
    }
}
