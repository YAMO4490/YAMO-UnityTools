# YAMO Unity Tools — 해설서

이 문서는 `com.yamo.unitytools` 패키지의 **전체 구조, 각 스크립트의 역할, 의존성, 수정 지점, 확장 방법**을 AI/사람 모두 빠르게 파악할 수 있도록 정리한 참고 문서다. 다른 컴퓨터·다른 프로젝트에서 AI를 활용해 이 패키지를 수정할 때 이 파일 하나만 읽히면 전체 맥락을 잡을 수 있도록 작성되었다.

---

## 1. 패키지 한눈에 보기

| 항목 | 내용 |
|---|---|
| 이름 | `com.yamo.unitytools` |
| 버전 | 0.3.0 (Runtime asmdef 도입, Bake & Prefab Pipeline, Master Hub 추가) |
| 대상 | Unity 2021.3 이상. Runtime 부분은 빌드에 포함, Editor 부분은 에디터 전용 |
| 어셈블리 | 4개 — Runtime / Editor (코어) / Physics.Editor / Biped.Editor |
| 메뉴 루트 | `Tools/YAMO/...` |
| 단축키 | `6` 단독 (커스터마이즈 가능, Edit ▸ Shortcuts) — Master Hub 토글 |

## 2. 폴더/파일 구조

```
Packages/com.yamo.unitytools/
├── package.json                                    ← 패키지 메타
├── HANDBOOK.md                                     ← 이 문서
├── YAMO.UnityTools.Editor.asmdef                   ← 코어 Editor asmdef (Runtime 참조)
├── Runtime/
│   ├── YAMO.UnityTools.Runtime.asmdef              ← 런타임 asmdef (제약 없음)
│   ├── BlendShapeLink/
│   │   └── BlendShapeLink.cs                       (MonoBehaviour, RequireComponent: SkinnedMeshRenderer)
│   └── YamoCam/
│       └── YamoCam.cs                              (MonoBehaviour, [ExecuteAlways])
└── Editor/
    ├── Internal/
    │   └── YamoDependencyDetector.cs               ← 외부 패키지 자동 감지 / define 주입
    ├── Animation/
    │   ├── FacialAnimationBaker.cs                 ← Tools/YAMO/Animation/Facial Animation Baker
    │   ├── ForearmHingeBaker.cs                    ← Tools/YAMO/Animation/Forearm Hinge Baker
    │   └── AnimBaker/
    │       ├── AnimClipCubicFitter.cs              ← Cubic Hermite curve fitter
    │       ├── AnimClipKeyReducer.cs               ← RDP/Cubic/Auto fit + 채널별 tolerance
    │       ├── AnimClipReducerWindow.cs            ← Tools/YAMO/Animation/Anim Clip Reducer (UI Toolkit)
    │       └── AnimYamlOptimizer.cs                ← .anim YAML 후처리 (정밀도 축소)
    ├── Assets/
    │   └── MaterialAndTextureCollectorWindow.cs    ← Tools/YAMO/Assets/Material And Texture Tool (4섹션 통합)
    ├── BlendShapeLink/
    │   └── BlendShapeLinkEditor.cs                 ← BlendShapeLink CustomEditor
    ├── Biped/
    │   ├── YAMO.UnityTools.Biped.Editor.asmdef     ← Biped 전용 asmdef (defineConstraints 게이트)
    │   ├── YamoToolHub.cs                          ← Tools/YAMO/⚡ Tool Hub (마스터 윈도우)
    │   └── BakeExport/
    │       ├── AvatarBakePipeline.cs               ← 베이크-임포트-마이그레이트-프리팹 오케스트레이션
    │       ├── AvatarBakePrefabWindow.cs           ← Tools/YAMO/Biped/Avatar Bake & Prefab Generator
    │       ├── AvatarBakePreUtilities.cs           ← 베이크 전 사전 정리 (중복이름·휴머노이드 리네임)
    │       └── YamoBoneNormalizer.cs               ← UniGLTF BoneNormalizer fork + 회전 보존 옵션
    ├── Bones/
    │   ├── HumanBoneRenamer.cs                     ← Tools/YAMO/Bones/Human Bone Renamer
    │   ├── YamoAssetChecker.cs                     ← Tools/YAMO/Bones/YAMO Asset Checker (5섹션 통합)
    │   └── YamoAssetCheckerCore.cs                 ← Asset Checker 코어 정적 헬퍼
    ├── Camera/
    │   ├── CameraCompositionWindow.cs              ← Tools/YAMO/Camera/Composition Overlay
    │   └── MainCameraScreenshotCapture.cs          ← Tools/YAMO/Camera/Capture Main Camera Screenshot
    ├── Physics/
    │   ├── YAMO.UnityTools.Physics.Editor.asmdef   ← Physics 전용 asmdef (defineConstraints 게이트)
    │   ├── AvatarMigrationCore.cs                  ← 마이그레이션 정적 코어 (Bake Pipeline 도 호출)
    │   └── AvatarPhysicsMigrator.cs                ← Tools/YAMO/Physics/Avatar Physics Migrator
    └── YamoCam/
        └── YamoCamEditor.cs                        ← YamoCam CustomEditor
```

## 3. 어셈블리 구조 (asmdef)

### 3-1. 4개 어셈블리

```
                                     ┌──────────────────────────┐
                                     │ YAMO.UnityTools.Runtime  │
                                     │ - 런타임 (빌드 포함)     │
                                     │ - 외부 참조 없음         │
                                     └────────────▲─────────────┘
                                                  │
            ┌─────────────────────────────────────┼─────────────────────────────┐
            │                                     │                             │
┌───────────┴───────────────┐  ┌──────────────────┴─────────────────┐  ┌────────┴─────────────────┐
│ YAMO.UnityTools.Editor    │  │ YAMO.UnityTools.Physics.Editor      │  │ YAMO.UnityTools.Biped     │
│ (루트 Editor asmdef)      │  │ - MagicaCloth2 + VRM 강타입 참조    │  │ .Editor                   │
│ - Editor 전용             │  │ - defineConstraints:                │  │ - Physics + UniGLTF +     │
│ - Runtime 참조            │  │   YAMO_HAS_MAGICACLOTH +            │  │   UniHumanoid + FBX 참조  │
│                           │  │   YAMO_HAS_VRM                      │  │ - 동일 defineConstraints   │
└───────────────────────────┘  └─────────────────────────────────────┘  └───────────────────────────┘
```

### 3-2. 어셈블리별 상세

| Asmdef | 위치 | platform | references | defineConstraints |
|---|---|---|---|---|
| `YAMO.UnityTools.Runtime` | `Runtime/` | (전체) | (없음) | (없음) |
| `YAMO.UnityTools.Editor` | 패키지 루트 | Editor | `YAMO.UnityTools.Runtime` | (없음) |
| `YAMO.UnityTools.Physics.Editor` | `Editor/Physics/` | Editor | `YAMO.UnityTools.Editor` + `MagicaClothV2` + `MagicaClothV2.Editor` + `VRM` | `YAMO_HAS_MAGICACLOTH`, `YAMO_HAS_VRM` |
| `YAMO.UnityTools.Biped.Editor` | `Editor/Biped/` | Editor | `YAMO.UnityTools.Editor` + `YAMO.UnityTools.Physics.Editor` + `MagicaClothV2(.Editor)` + `VRM` + `UniGLTF` + `UniHumanoid` + `Unity.Formats.Fbx.Editor` | `YAMO_HAS_MAGICACLOTH`, `YAMO_HAS_VRM` |

### 3-3. 네임스페이스 규칙

- Runtime: `YAMO.UnityTools`
- Editor: `YAMO.UnityTools.Editor`
- Internal infra: `YAMO.UnityTools.Editor.Internal`

> 일부 정책: 각 도구가 통일된 네임스페이스를 쓰도록 정리됨 (이전의 `Streamingle.AnimationTools`, `YAMO.BlendShapeLink` 등은 `YAMO.UnityTools[.Editor]` 로 통합).

## 4. 외부 의존성

### 4-1. 의존하는 외부 패키지

| 패키지 | 어셈블리명 | 용도 | 자동 감지 심볼 |
|---|---|---|---|
| MagicaCloth2 | `MagicaClothV2` | Avatar physics migration / cloth components | `YAMO_HAS_MAGICACLOTH` |
| UniVRM (0.x) | `VRM` | VRMSpringBone migration / Humanoid 생성 | `YAMO_HAS_VRM` |
| UniGLTF | `UniGLTF` | BoneNormalizer 베이스, Mesh extension | (Biped asmdef 강참조) |
| UniHumanoid | `UniHumanoid` | Humanoid Avatar 생성 (`AvatarDescription`) | (Biped asmdef 강참조) |
| Unity FBX Exporter | `Unity.Formats.Fbx.Editor` (`com.unity.formats.fbx`) | 베이크된 정규화 GameObject → FBX 출력 | (Biped asmdef 강참조) |

### 4-2. `YamoDependencyDetector` — 자동 감지 + define 주입

`Editor/Internal/YamoDependencyDetector.cs` 가 `[InitializeOnLoad]` 로 Unity 로드/리컴파일 직후 실행:

1. `AppDomain.CurrentDomain.GetAssemblies()` 로 현재 로드된 어셈블리 목록 스캔
2. 표에 정의된 `(어셈블리 이름, define 심볼)` 쌍 비교
3. 존재 O + define X → define 추가 / 존재 X + define O → define 제거
4. 변경된 경우 `PlayerSettings.SetScriptingDefineSymbols` 로 기록

```csharp
("MagicaClothV2", "YAMO_HAS_MAGICACLOTH"),
("VRM",           "YAMO_HAS_VRM"),
```

**다른 의존성을 추가하려면** `Detectors` 배열에 쌍을 추가하면 끝.

### 4-3. 게이팅 효과

| 환경 | YAMO.UnityTools.Editor (코어) | Physics.Editor | Biped.Editor |
|---|---|---|---|
| MagicaCloth + VRM 모두 있음 | ✅ 컴파일 | ✅ 컴파일 | ✅ 컴파일 |
| MagicaCloth만 있음 | ✅ | ❌ (전체 비활성) | ❌ |
| VRM만 있음 | ✅ | ❌ | ❌ |
| 둘 다 없음 | ✅ | ❌ | ❌ |

→ 코어는 무조건 동작. Physics/Biped 기능은 둘 다 있을 때만. asmdef 레벨에서 자동 처리되므로 코드 안에 `#if` 가드 불필요.

## 5. 각 스크립트 상세 해설

### 5-1. Runtime 컴포넌트

#### `Runtime/BlendShapeLink/BlendShapeLink.cs`
- **클래스**: `YAMO.UnityTools.BlendShapeLink` (MonoBehaviour, `[RequireComponent(typeof(SkinnedMeshRenderer))]`)
- **AddComponent 메뉴**: `YAMO/BlendShape Link`
- 특정 BlendShape 값 변화에 반응해 다른 BlendShape 값을 실시간 연동.
  - **Multiply 모드**: target 에 `(source × multiplier)` 기여, source 유지
  - **Override 모드**: target 에 기여 후 source 를 0 으로 리셋 (값 이전/swap)
- 같은 target 에 여러 규칙 적용 시 **Max 방식** 선택.
- 플레이 모드에서만 동작 (`LateUpdate`).
- **수정 포인트**: `LinkRule` 구조 / `LateUpdate()` 의 3단계 처리 로직.

#### `Runtime/YamoCam/YamoCam.cs`
- **클래스**: `YAMO.UnityTools.YamoCam` (MonoBehaviour, `[ExecuteAlways]`)
- **AddComponent 메뉴**: `YAMO/YAMO Cam`
- 카메라 컨트롤 4 모듈:
  - **Follow**: 타겟 평균 위치 추적 + 거리 기반 elasticity, axis 별 ratio
  - **LookAt**: 타겟 응시 + axis 별 회전 ratio
  - **Orbital**: 수평 360° 루프 + 수직 ping-pong (sine easing)
  - **Noise (Hand-held)**: Perlin 기반 위치/회전 떨림
- 에디트 모드에서도 동작 (`updateInEditMode` 토글, `EditorApplication.update` 후크 사용).
- **수정 포인트**: 각 `Apply*()` 메서드 / 보간 수식 (`Lerp` t 계산).

### 5-2. Editor 인프라

#### `Editor/Internal/YamoDependencyDetector.cs`
- `[InitializeOnLoad]` + `EditorApplication.delayCall` 로 초기화 타이밍 안전화.
- `Detectors` 배열이 "이 패키지는 이 define 이 주입되어야 한다" 의 single source of truth.
- **수정 빈도**: 새 외부 패키지 통합 시에만.

### 5-3. Editor / Animation

#### `Animation/FacialAnimationBaker.cs` — `Tools/YAMO/Animation/Facial Animation Baker`
- BlendShape 키 다수로 비대해진 페이셜 .anim 클립을, 캐릭터 독립 셸 하이어라키를 자동 생성한 뒤 Unity FBX Exporter 로 .fbx 출력 → 용량 축소.
- `EditorCurveBinding` 에서 path/component/blendshape 이름 역추적 → 임시 셸 GameObject → 임시 AnimatorController 래핑 → FBX Exporter 호출 → 임시 자산 정리.
- 결과 FBX 의 경로 바인딩이 원본 .anim 그대로라 **어떤 캐릭터에 올려도 동일 적용**.
- FBX Exporter 는 리플렉션으로 선택적 사용 — 없으면 에러 메시지 후 종료.
- **수정 포인트**: keyframe stride / include dictionary / 셸 생성 로직.

#### `Animation/ForearmHingeBaker.cs` — `Tools/YAMO/Animation/Forearm Hinge Baker`
- Humanoid 클립의 Forearm 비-힌지 회전 제거 → Biped 단축 힌지와 호환되는 Generic 클립 생성.
- 알고리즘: 매 프레임 샘플링 → Forearm 힌지각 해석적 풀이 (Atan2) → UpperArm 최소 보정 → Hand 월드 회전 복원.
- 외부 의존성 없음.
- **수정 포인트**: `armTriplets` (다리 등 추가) / `axisVec` 결정부 / theta 풀이 임계값.

#### `Animation/AnimBaker/` — `Tools/YAMO/Animation/Anim Clip Reducer`
머슬 클립 압축 (4 파일):

| 파일 | 역할 |
|---|---|
| `AnimClipCubicFitter.cs` | Cubic Hermite curve fitter (Schneider 1990 기반). 양 끝 키의 tangent 를 LSQ 로 풀이, overshoot sub-sampling 으로 발산 방지 |
| `AnimClipKeyReducer.cs` | 핵심 reduce 엔진. RDP / Cubic / Auto (둘 다 돌려 byte 적게 나오는 쪽) 선택. 채널별 tolerance (Muscle/Spine/RootPos/RootRot/Generic). DropUnusedChannels (전체가 0 근처 머슬은 삭제). Resample. Pre-smoothing. |
| `AnimClipReducerWindow.cs` | UI Toolkit 기반 EditorWindow. 5 가지 quality preset (Lossless/Standard/Aggressive/High/Extreme/Custom). 정밀도/추가옵션/실험적 foldout. |
| `AnimYamlOptimizer.cs` | .anim YAML 후처리. value/inSlope/outSlope 유효숫자 축소, near-zero slope snap to 0, m_EditorCurves 제거 |

- **수정 포인트**: preset 값 (`ApplyQualityPreset`) / 채널 분류 (`ToleranceFor`/`IsRootChannel`/`IsSpineMuscle`).

### 5-4. Editor / Assets

#### `Assets/MaterialAndTextureCollectorWindow.cs` — `Tools/YAMO/Assets/Material And Texture Tool`
4 섹션 통합 (foldout):

1. **머테리얼/텍스처 관리**: Prefab 하위 머티리얼·텍스처 수집·중복 검출·복사·이동
2. **PSD → PNG 변환**: GUID 보존하며 PSD 를 PNG 로 일괄 변환 (메타 이식 + 원본 삭제)
3. **텍스처 리사이즈 (2048 초과)**: NormalMap 채널 왜곡 방지 (Default 타입 임시 변경 + sRGB false 강제), HDR→EXR 자동 변환
4. **닐로툰 매트캡 자동 인식기**: lilToon `_UseMatCap`/`_UseMatCap2nd` 슬롯 → NiloToon `_BaseMapStackingLayer[n]*` 로 자동 주입. lilToon 셰이더 임시 교체 후 프로퍼티 추출 (Shader.Find 기반, asmdef 참조 X)

- 외부 의존성 없음. `AssetDatabase`, `TextureImporter` 기반.
- **수정 포인트**: 각 섹션은 private 메서드 뭉치로 분리. UI 진입점은 `DrawGUI()` 의 `secNFoldout`.

### 5-5. Editor / Bones

#### `Bones/HumanBoneRenamer.cs` — `Tools/YAMO/Bones/Human Bone Renamer`
- Unity Humanoid 표준 본 이름 (`Hips`, `LeftUpperArm` 등) 으로 자동 rename + 사후 진단.
- **UpperChest 우회**: 표준 `UpperChest` 가 아닌 `Chest_Secondary` 로 명명 → Unity 자동 매핑이 슬롯을 비워두게 유도.
- **Pre-flight 체크**: Spine→Neck, Chest→Head 의 chain intermediates 검사. 정상 ≤ 2, 그보다 크면 mocap 등 비정상 chain 으로 판단 → 중단 + 팝업.
- **사후 진단**: Spine/Chest 이름 카운트 (각 1 이어야 정상), LeftToes/RightToes 검출 여부 → 팝업.
- 3ds Max Biped 매핑 (`bipedMapping`), Mixamo 등 자동 인식.

#### `Bones/YamoAssetChecker.cs` — `Tools/YAMO/Bones/YAMO Asset Checker`
**5 섹션 통합** (foldout). 이전의 `ObjectNameModifier` / `MissingScriptRemover` / `FindMissingBones` / `FindUnusedBones` 가 모두 흡수됨.

| 섹션 | 기능 |
|---|---|
| 1. Object Name Tools | Prefix/Suffix, Remove first/last char, Spaces→Underscore, Sort children, Humanoid scale check (Selection 기반) |
| 2. Duplicate Names | 트리 내 중복 이름 검출 + 자동 리네임 (`_1`, `_2`...) |
| 3. Unused Bones | 어떤 SMR 도 참조 안 하는 Transform 검출 + Selection 으로 추가. 부분문자열 / Magica / VRMSpringBone 컴포넌트 제외 옵션 |
| 4. Missing / Disabled Scripts | Missing MonoBehaviour 카운트/제거, Disabled MonoBehaviour 제거 |
| 5. Missing Bones (SMR) | 씬/Selection 의 SkinnedMeshRenderer 의 `bones[i] == null` 또는 rootBone null 검출 |

#### `Bones/YamoAssetCheckerCore.cs`
- 위 5 섹션이 호출하는 정적 헬퍼 모음 (UI 무관 순수 로직).
- API: `FindDuplicateNames`, `AutoRenameDuplicates`, `FindHumanoidBonesWithNonOneScale`, `FindUnusedBones`, `CountScripts/RemoveMissingScripts/RemoveDisabledScripts/RemoveAllScripts`, `CheckMissingBonesInScene/Children` + nested type `MissingBoneResult`, `UnusedBoneOptions`.

### 5-6. Editor / Physics (게이트: MagicaCloth + VRM)

#### `Physics/AvatarMigrationCore.cs`
**마이그레이션 정적 코어** (Physics asmdef 안에 거주, Biped Pipeline 도 이 코어를 호출).

API:
- `ValidateNoDuplicateNames(root, log) → bool`
- `BuildBoneMap(source, target, log) → Dictionary<Transform, Transform>` (1순위 Humanoid, 2순위 이름 기반)
- `MigrateColliders(srcRoot, targetRoot, boneMap, log)` — Magica 3종 + VRMSpringBoneColliderGroup. **콜라이더 GO 의 월드 포즈 보존 트릭**: `Object.Instantiate(src.gameObject, src.position, src.rotation)` + `SetParent(parent, true)` — 본 회전이 베이크로 바뀌어도 부착 위치 유지.
- `MigrateMagicaCloth` / `MigrateVRMSpringBone` — 한 GameObject 에 여러 인스턴스 케이스 보존 (clearedDsts HashSet 으로 재실행 누적 방지)
- `MigrateActiveStates(boneMap, log)` — 매핑된 모든 쌍의 `activeSelf` 적용
- `MigrateConstraints(srcRoot, boneMap, log)` — Unity 빌트인 6종 (`PositionConstraint` 등). **좌표계 드리프트 회피 정책**: sources + 각 weight + overall weight + flags 만 복사. offset/rest/aim/up vectors 등은 미복사 (AddComponent 시 자동 캡처되는 기본값 사용).
- `MigrateBlendShapes` — name-based 매칭 (인덱스 X)
- `IMigrationLog` 인터페이스 + `DebugMigrationLog` 기본 구현

#### `Physics/AvatarPhysicsMigrator.cs` — `Tools/YAMO/Physics/Avatar Physics Migrator`
- 위 `AvatarMigrationCore` 를 호출하는 EditorWindow.
- 추가 편의 기능: Analyze (이름 매칭률·중복·컴포넌트 수 집계), Auto Create MagicaCloth PreBuild Data, Collider Cleanup (선택 객체 하위 콜라이더 일괄 삭제, 본 보호), BlendShape 리셋 등.

### 5-7. Editor / Biped — Avatar Bake & Prefab Pipeline (게이트: MagicaCloth + VRM)

#### `Biped/YamoToolHub.cs` — `Tools/YAMO/⚡ Tool Hub`
**마스터 윈도우 — 4개 탭**:

| 탭 | 임베드 도구 | 방식 |
|---|---|---|
| Avatar Bake & Prefab | `AvatarBakePrefabWindow` | DrawGUI() 임베드 |
| Material & Texture | `MaterialAndTextureCollectorWindow` | DrawGUI() 임베드 |
| Asset Checker | `YamoAssetChecker` | DrawGUI() 임베드 |
| Animation | FacialAnimationBaker, ForearmHingeBaker, AnimClipReducerWindow | Launcher (별도 창 열기) — UI Toolkit 기반 도구 포함이라 IMGUI 임베드 곤란 |

- **단축키**: `[Shortcut]` 어트리뷰트로 Unity ShortcutManager 등록. 기본값 `6` 단독, 식별자 `YAMO/Open Tool Hub`. `Edit ▸ Shortcuts` 에서 자유 재할당.
- 임베드 도구는 `ScriptableObject.CreateInstance<T>()` 로 비표시 인스턴스 생성 → public `DrawGUI()` 호출.
- **확장 방법**: 새 도구 추가 시 도구의 `OnGUI` 본체를 `public void DrawGUI()` 로 분리, `Tab` enum + `TabLabels` + 인스턴스 필드 + `OnEnable/OnDisable` + switch 분기 5 곳 추가.

#### `Biped/BakeExport/AvatarBakePrefabWindow.cs` — `Tools/YAMO/Biped/Avatar Bake & Prefab Generator`
풀 파이프라인 EditorWindow. 사전 정리(Pre-Bake Utilities) 섹션 + 출력 옵션 + 마이그레이션 카테고리 + 파이프라인 옵션 + 회전 보존 + Run 버튼.

UI 구성:
- **Avatar Root** (GameObject)
- **Pre-Bake Utilities** — Find Duplicate Names, Auto-Rename Duplicates, Rename to Unity Humanoid Standard
- **Output**: FBX Path + Prefab Path (자동 채움 = `Assets/{name}/{name}.fbx`/.prefab)
- **Avatar Mode**: Auto / Humanoid / Generic, Force T-Pose
- **Rotation Preservation**: Preserve All Rotations (기본 ON) + By Name Substring (옵션)
- **Migrate**: Active States / BlendShapes / Physics / Constraints (각 토글)
- **Pipeline Options**: Validate Unique Names, Zero BlendShapes Before Bake, Restore Source After Bake, Update When Offscreen (Prefab), Material Import: None
- **Log Panel** + Clear Log

#### `Biped/BakeExport/AvatarBakePipeline.cs`
정적 오케스트레이터. 시퀀스:

```
1) Pre-flight: 중복 이름 검사
2) Snapshot — opt.Source 를 Object.Instantiate → "{name}__OriginalState" (씬 보존, source-of-truth)
   prefab instance 면 UnpackPrefabInstance 처리
3) Activate-All on live source — 비활성 자식 누락 방지
4) Zero BlendShape weights — BoneNormalizer.BakeMesh 가 현재 포즈를 rest 로 굽는 버그 회피
5) (옵션) T-Pose enforce
6) Bake — YamoBoneNormalizer.Execute (NormalizeOptions: 회전 보존 정책)
7) Export FBX — ModelExporter.ExportObject (UseMayaCompatibleNames=false 점 보존, Format=Binary)
8) (옵션) Restore source — snapshot 기준 lockstep 으로 active state + BlendShape weight 되돌림
9) Import + ConfigureModelImporter — animationType (Humanoid/Generic), materialImportMode (옵션)
10) Instantiate FBX → targetInstance 씬 배치
11) BuildBoneMap(snapshot → targetInstance)
12) Migrate (snapshot 을 source-of-truth):
    - Active States  ← MigrateActiveStates
    - BlendShape weights  ← MigrateBlendShapes
    - Physics  ← MigrateColliders + MigrateMagicaCloth + MigrateVRMSpringBone
    - Constraints  ← MigrateConstraints
13) (옵션) updateWhenOffscreen = true 일괄 적용
14) PrefabUtility.SaveAsPrefabAsset (덮어쓰기)
```

진행률은 `EditorUtility.DisplayProgressBar` 로 표시. snapshot/targetInstance 는 성공 후에도 씬에 보존되어 사용자가 비교/검수 가능.

#### `Biped/BakeExport/AvatarBakePreUtilities.cs`
베이크 직전 정리용 정적 헬퍼:
- `FindDuplicateNames` / `AutoRenameDuplicates`
- `GetHumanBones(target)` — Animator 1순위 + Biped 이름 fallback
- `RenameToUnityHumanoidNames(target) → HumanoidRenameReport`
  - `HumanoidRenameReport`: Aborted, AbortReason, SpineToNeckIntermediates, ChestToHeadIntermediates, RenamedCount, BonesDetected, SpineCount, ChestCount, HasUpperChest, UpperChestRenamedToSecondary, LeftToesDetected, RightToesDetected
  - **Pre-flight**: Spine→Neck, Chest→Head intermediates ≤ 2 검사. 초과 시 abort (mocap 5-spine, 2-neck 등 비정상 chain 차단)
  - **UpperChest 우회**: 표준 `UpperChest` 대신 `Chest_Secondary` 로 명명
- 상수 `UpperChestReplacementName = "Chest_Secondary"`, `MaxChainIntermediatesNormal = 2`

#### `Biped/BakeExport/YamoBoneNormalizer.cs`
**UniGLTF `BoneNormalizer` 의 fork**. 위치: `Assets/External/UniGLTF/Runtime/MeshUtility/BoneNormalizer.cs` 가 원본.

주요 차이:
- `NormalizeOptions { PreserveAllRotations, RotationFilter }` 추가
- `CopyAndBuild` 에서 필터 통과 시 `dstChild.transform.rotation = child.rotation` 으로 회전 보존
- `NormalizeSkinnedMesh` 에서 SMR 의 회전이 보존되면 mesh ApplyMatrix 의 `m` 을 identity (회전 보정 생략, BlendShape delta 도 자동으로 회전 미적용)
- `NormalizeNoneSkinnedMesh` 에서 회전 보존 시 `Matrix4x4.Scale(lossyScale)` 만 mesh 에 적용

스케일은 항상 (1,1,1) 로 정규화. 회전은 옵션에 따라 보존.

### 5-8. Editor / 그 외 Custom Editors

#### `BlendShapeLink/BlendShapeLinkEditor.cs`
- `BlendShapeLink` 의 CustomEditor.
- 검색 가능한 dropdown (Unity AdvancedDropdown) 으로 BlendShape 인덱스 선택.
- LinkRule 별 사용자 친화적 인스펙터 (▲▼ 순서 변경, X 삭제, Mode/Multiplier).

#### `YamoCam/YamoCamEditor.cs`
- `YamoCam` 의 CustomEditor.
- 4 섹션 (Follow / LookAt / Orbital / Noise) 별 활성 토글 + 옵션. 활성 모듈만 펼쳐 표시.

### 5-9. Editor / Camera

#### `Camera/MainCameraScreenshotCapture.cs` — `Tools/YAMO/Camera/Capture Main Camera Screenshot`
- 에디트 모드에서 `Camera.main` 이 보는 화면을 즉시 PNG 로 저장.
- Game View 해상도를 리플렉션으로 읽고, 실패하면 카메라 pixel size → 1920×1080 순서로 fallback.
- 저장 위치: 프로젝트 루트 `Assets/Screenshots/{Scene}_{Camera}_{yyyyMMdd_HHmmss}.png`.
- ShortcutManager 항목: `YAMO/Capture Main Camera Screenshot` — 기본 키 없음, `Edit ▸ Shortcuts` 에서 원하는 핫키 지정.
- Unity 2021.2 이상에서는 Scene View Overlay `YAMO Camera` 에 `Shot` 버튼을 등록.

## 6. 공통 컨벤션

- **네임스페이스**: 런타임 = `YAMO.UnityTools` / 에디터 = `YAMO.UnityTools.Editor` / 비공개 = `YAMO.UnityTools.Editor.Internal`
- **MenuItem 경로**: `Tools/YAMO/<카테고리>/<기능명>` (마스터 Hub 는 `Tools/YAMO/⚡ Tool Hub`)
- **AddComponent 메뉴**: `YAMO/<컴포넌트명>`
- **파일명 = 주 클래스명**. 파일에는 주 클래스 하나만 둠을 원칙.
- **외부 의존성 접근 원칙**:
  - 코어 (`YAMO.UnityTools.Editor`) 는 외부 패키지 직접 참조 금지. 리플렉션 (`typeof(T).Name == "..."`) 우선.
  - 강타입 API 가 꼭 필요하면 별도 게이트 asmdef (Physics / Biped) 로 격리 + `defineConstraints` 로 조건부 컴파일.
  - `using MagicaCloth2;` / `using VRM;` 같은 직접 import 는 게이트 asmdef 안에서만.
- **Hub 임베드 패턴**: 새 IMGUI 도구를 Hub 에 통합하려면 `OnGUI()` 본체를 `public void DrawGUI()` 로 분리.

## 7. 자주 하는 확장 작업 템플릿

### 7-1. 새 메뉴 툴 추가 (외부 의존성 없음, 코어 영역)

```csharp
// Editor/Bones/NewTool.cs
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class NewTool : EditorWindow
    {
        [MenuItem("Tools/YAMO/Bones/New Tool")]
        public static void Open() => GetWindow<NewTool>("New Tool");

        private void OnGUI() => DrawGUI();
        public void DrawGUI() { /* ... */ }   // ← Hub 임베드 대비
    }
}
```

### 7-2. 새 외부 패키지 의존성 추가

1. `YamoDependencyDetector.Detectors` 에 `(어셈블리명, define)` 추가.
2. 신규 폴더 + 별도 asmdef 분리:
   ```
   Editor/FinalIK/
   ├── YAMO.UnityTools.FinalIK.Editor.asmdef
   │   - references: [YAMO.UnityTools.Editor, FinalIK]
   │   - defineConstraints: [YAMO_HAS_FINALIK]
   └── YourFinalIKTool.cs
   ```
3. 파일 내부 `#if` 가드 불필요 (asmdef 가 조건부 컴파일 담당).

### 7-3. 새 런타임 컴포넌트 추가

1. `Runtime/YourFeature/YourComponent.cs` — namespace `YAMO.UnityTools`.
2. 필요 시 CustomEditor 는 `Editor/YourFeature/YourComponentEditor.cs` — namespace `YAMO.UnityTools.Editor`, `using YAMO.UnityTools;`.

### 7-4. 새 Hub 탭 추가

`YamoToolHub.cs` 에서:
1. `Tab` enum 에 항목 추가
2. `TabLabels` 배열에 표시 이름 추가
3. 인스턴스 필드 (`private MyWindow _myInstance`) 추가
4. `OnEnable` / `OnDisable` 에 생성/해제 라인 추가
5. `OnGUI` switch 에 `case Tab.MyTab: _myInstance.DrawGUI(); break;` 추가

해당 도구가 UI Toolkit (`CreateGUI`) 기반이면 launcher 로 (`DrawAnimationTab` 패턴 참고).

## 8. 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| `The type or namespace 'MagicaCloth2' could not be found` | `YamoDependencyDetector` 가 아직 동작 못 한 초기 상태. Unity 한 번 재컴파일 후 해결 |
| `YAMO_HAS_*` 심볼이 안 생김 | 1) 외부 어셈블리 이름 정확한지 확인 (`MagicaClothV2`, `VRM`) 2) `Detectors` 배열 오타 3) Player Settings → Scripting Define Symbols 직접 확인 |
| `AvatarPhysicsMigrator` / Hub 등 메뉴가 안 보임 | 두 패키지 (MagicaCloth, VRM) 중 하나라도 없으면 Physics/Biped asmdef 가 비활성. **정상 동작**. |
| 베이크 결과물에 BlendShape 가 죽음 (예: 눈감기 100 적용 후 베이크하면 EyesClose 가 0 으로 굽혀짐) | `Zero BlendShapes Before Bake` 옵션이 OFF 임. 기본 ON 상태로 사용 |
| 본 이름의 점 (`.`) 이 `_` 로 바뀜 | 이전 버전 잔재. 현재는 `UseMayaCompatibleNames = false` 로 export 옵션 명시되어 점 보존됨 |
| 머티리얼 슬롯 갯수/이름이 사라짐 | `Material Import: None` 토글 OFF 로 전환 (기본 OFF). Unity 기본 임포트가 슬롯 정보 보존 |
| Secondary 의 VRMSpringBone 이 1 개로 줄어듦 | 해결됨. `MigrateVRMSpringBone` 가 `AddComponent` 로 항상 새로 추가, `clearedDsts` 로 재실행 누적만 방지 |
| Spine→Neck 본이 5 개인데 휴머노이드 리네임이 안 됨 | 의도된 abort. 본 계층 정리 후 재시도. 정상 chain intermediates ≤ 2 |
| Hub 단축키 충돌 | `Edit ▸ Shortcuts` → "YAMO/Open Tool Hub" 검색 → 다른 키로 변경 |

## 9. 변경 이력 요약

### 0.3.0
- Runtime asmdef 도입 (`YAMO.UnityTools.Runtime`)
- BlendShapeLink, YamoCam 런타임 컴포넌트 추가
- Avatar Bake & Prefab Pipeline 추가 (신규 Biped asmdef)
  - `AvatarBakePrefabWindow`, `AvatarBakePipeline`, `AvatarBakePreUtilities`, `YamoBoneNormalizer`
  - 마이그레이션 코어 (`AvatarMigrationCore`) 를 `AvatarPhysicsMigrator` 에서 추출, Bake Pipeline 도 같은 코어 호출
- Master Hub (`YamoToolHub`) 추가 — 4 탭 통합 + 단축키
- Anim Clip Reducer (구 머슬 클립 압축기) 통합 — `Editor/Animation/AnimBaker/`
- 통합 도구로 인한 기존 도구 흡수:
  - `ObjectNameModifier` + `MissingScriptRemover` + `FindMissingBones` + `FindUnusedBones` → `YamoAssetChecker` (5 섹션)
  - `NilotoonMaterialMatcapSetter` → `MaterialAndTextureCollectorWindow` (4 섹션)
- 폴더 정리: `Hierarchy/`, `Layout/`, `Materials/` 폴더 제거 (도구 흡수 + 미사용 도구 제거)
- 네임스페이스 통일: `Streamingle.AnimationTools`, `YAMO.BlendShapeLink` → `YAMO.UnityTools[.Editor]`

### 0.2.0
- Stream Deck 통합 제거
- 외부 패키지 자동 감지 도입 (`YamoDependencyDetector`)
- Physics asmdef 분리 (`YAMO.UnityTools.Physics.Editor`)

---

**이 문서 업데이트 원칙**: 스크립트 추가·제거·이름 변경하면 §2(폴더 구조)와 §5(상세 해설)에 즉시 반영. asmdef 변경은 §3, 의존성 변경은 §4, 컨벤션은 §6, 변경이 누적되면 §9 에 한 줄 추가.
