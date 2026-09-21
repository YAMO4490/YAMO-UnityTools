# YAMO Unity Tools — 해설서

이 문서는 `com.yamo.unitytools` 패키지의 **전체 구조, 각 스크립트의 역할, 의존성, 수정 지점, 확장 방법**을 AI/사람 모두 빠르게 파악할 수 있도록 정리한 참고 문서다. 다른 컴퓨터·다른 프로젝트에서 AI를 활용해 이 패키지를 수정할 때 이 파일 하나만 읽히면 전체 맥락을 잡을 수 있도록 작성되었다.

---

## 1. 패키지 한눈에 보기

| 항목 | 내용 |
|---|---|
| 이름 | `com.yamo.unitytools` |
| 버전 | 0.10.0 (Scale Bake, Avatar 전용 FBX, NiloToon 컨트롤러 이관) |
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
├── Tests/Editor/BlendShapeCurveRemapperTests.cs  <- EditMode tests
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
    │   ├── MocapToBipedFbxPipelineWindow.cs        ← Tools/YAMO/Animation/Mocap to Biped FBX Pipeline (탭: OptiTrack / MMRP / FBX 애니메이션 설정)
    │   ├── MocapToBipedFbxPipeline.cs              ← Edit Mode 동기 파이프라인 (바인딩 → Hinge → Max FBX)
    │   ├── MocapToBipedFbxPlayModeRunner.cs        ← Play Mode 배치 러너
    │   ├── OptiTrackMotionBindingService.cs        ← 모션 FBX 바인딩 (OptiTrack 접두사 / MMRP 표준 본 이름)
    │   ├── FbxAnimationSetupService.cs             ← 클립 임포트 설정 (압축 Off · 클립명 · Root Bake)
    │   ├── BlendShapeCurveRemapper/
    │   │   ├── BlendShapeCurveRemapper.cs            <- exact binding discovery and remap core
    │   │   ├── BlendShapeCurveRemapperWindow.cs      <- Tools/YAMO/Animation/BlendShape Curve Remapper
    │   │   ├── README.md
    │   └── AnimBaker/
    │       ├── AnimClipCubicFitter.cs              ← Cubic Hermite curve fitter
    │       ├── AnimClipKeyReducer.cs               ← RDP/Cubic/Auto fit + 채널별 tolerance
    │       ├── AnimClipReducerWindow.cs            ← Tools/YAMO/Animation/Anim Clip Reducer (UI Toolkit)
    │       └── AnimYamlOptimizer.cs                ← .anim YAML 후처리 (정밀도 축소)
    ├── Assets/
    │   └── MaterialAndTextureCollectorWindow.cs    ← Tools/YAMO/Assets/Material And Texture Tool (3섹션 통합)
    ├── BlendShapeLink/
    │   └── BlendShapeLinkEditor.cs                 ← BlendShapeLink CustomEditor
    ├── Biped/
    │   ├── YAMO.UnityTools.Biped.Editor.asmdef     ← Biped 전용 asmdef (defineConstraints 게이트)
    │   ├── YamoToolHub.cs                          ← Tools/YAMO/⚡ Tool Hub (마스터 윈도우)
    │   └── BakeExport/
    │       ├── AvatarBakePipeline.cs               ← 베이크-임포트-마이그레이트-프리팹 오케스트레이션
    │       ├── AvatarBakePrefabWindow.cs           ← Tools/YAMO/Biped/Avatar Bake & Prefab Generator
    │       ├── AvatarScaleBakeUtility.cs           ← 루트 배율 베이크·원본 프리팹 옆 자동 저장
    │       ├── AvatarBakeFbxSetup.cs               ← 임포트·머티리얼 매핑·Bip001 Avatar FBX
    │       ├── AvatarBakeComponentMigration.cs     ← 선택적 NiloToon 컨트롤러 복사·내부 참조 재연결
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
  - **Follow**: 타겟 평균 위치 추적 + 거리 기반 elasticity, axis 별 ratio. `positionOffset` 및 축 마스킹은 타겟 **로컬 공간** 기준 → 타겟을 회전시켜도 카메라와의 상대 위치 관계가 유지됨.
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

#### `Animation/MocapToBipedFbxPipelineWindow.cs` — `Tools/YAMO/Animation/Mocap to Biped FBX Pipeline` (단축키 `8`)
- 상단 `GUILayout.Toolbar` 탭 3개: **OptiTrack 파이프라인 / MMRP 파이프라인 / FBX 애니메이션 설정**. 파이프라인 탭은 큐를 따로 갖고 대상 Biped·출력·옵션은 공유.
- `MocapSourceFormat`(OptiTrack / MMRP)이 `MocapPipelineSettings.SourceFormat`으로 바인딩 서비스까지 전달됨. 큐에 넣을 때 `DetectSourceFormat`으로 다른 탭 파일을 걸러냄.
- `OptiTrackMotionBindingService`: OptiTrack은 `{접두사}_…` 테이블 + 스파인 강제 지정, MMRP는 `StandardHumanBoneNames` 정확 일치 매핑. 두 형식 모두 Eye/Jaw/UpperChest 제거(`RemapHumanBones`), `_T`/`_Backup` 생성 규칙은 공유.
- FBX 애니메이션 설정 탭은 예전 `FbxAnimSetupWindow`(단축키 `9`)를 통합한 것. 클립 설정 로직은 `FbxAnimationSetupService`로 분리되어 바인딩 서비스와 공유.
- **수정 포인트**: 새 소스 형식 추가 시 `MocapSourceFormat` 값 + `DetectSourceFormat` 규칙 + `BuildTPoseAvatar` 분기 + 창 탭 라벨.

#### `Animation/ForearmHingeBaker.cs` — `Tools/YAMO/Animation/Forearm Hinge Baker`
- Humanoid 클립의 Forearm 비-힌지 회전 제거 → Biped 단축 힌지와 호환되는 Generic 클립 생성.
- 알고리즘: 매 프레임 샘플링 → Forearm 힌지각 해석적 풀이 (Atan2) → UpperArm 최소 보정 → Hand 월드 회전 복원.
- 외부 의존성 없음.
- **수정 포인트**: `armTriplets` (다리 등 추가) / `axisVec` 결정부 / theta 풀이 임계값.

#### `Animation/BlendShapeCurveRemapper/` — `Tools/YAMO/Animation/BlendShape Curve Remapper`
- Reads `SkinnedMeshRenderer` `blendShape.*` bindings from an assigned AnimationClip, grouped by exact Mesh binding path.
- Select one Mesh track first, then one or more blend-shape properties. The exact `(path, propertyName)` pair prevents same-named properties on other Meshes from changing.
- Default mapping: `<= 10 -> 0`, `10..85 -> 0..20`, `>= 85 -> 100`; every threshold and output is configurable.
- Preserves key times, forces processed keys to unweighted Linear tangents, and supports copy output or Undo-enabled native `.anim` overwrite.

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
3 섹션 통합 (foldout):

1. **머테리얼/텍스처 관리**: Prefab 하위 머티리얼·텍스처 수집·중복 검출·복사·이동
2. **PSD → PNG 변환**: GUID 보존하며 PSD 를 PNG 로 일괄 변환 (메타 이식 + 원본 삭제)
3. **텍스처 리사이즈 (2048 초과)**: NormalMap 채널 왜곡 방지 (Default 타입 임시 변경 + sRGB false 강제), HDR→EXR 자동 변환
4. **Convert NiloToon**: 머티리얼 백업(`Assets/MaterialsBackup`) → NiloToon Auto setup(변환) → 텍스처 없는 활성 기능 정리. UI 만 담당, 로직은 `NiloToonConvertCore`.

#### `Assets/NiloToonConvertCore.cs`
- 정적 API: `ConvertAvatar(root)`(백업→변환→정리), `CleanupOnly(root)`, `LoadBackupSet(folder, out error)`, `RestoreFromBackup(BackupSet)`, `IsSupported`/`InstalledNiloToonVersion`/`IsVersionSupported(text)`. 결과는 `Result.ToText()`.
#### `Assets/LilToonMainColorBaker.cs`
- `ConvertAvatar(root, bakeLilToonMainColor = true)` 가 **백업 後·Auto setup 前**에 호출. lilToon 의 메인 컬러(`_Color`)·`_MainTexHSVG`·그라디언트 맵을 메인 텍스처에 굽고 머티리얼을 색=흰색/HSVG 기본값으로 되돌린다(lilToon 인스펙터 [굽기] 의 헤드리스 재현).
- 이유: lilToon `_Color` 는 `[lilHDR]`(Unity 는 sRGB 로 보고 리니어 변환), NiloToon `_BaseColor` 는 `[HDR]`(변환 없음) → 같은 값을 옮기면 NiloToon 에서 밝고 탁해진다. HSVG 는 구현이 다르고 그라디언트는 NiloToon 이 옮기지 않는다.
- lilToon 을 **하드 참조하지 않는다** — 베이커 셰이더를 이름(`Hidden/ltsother_baker`)으로만 찾고, 렌더 경로는 `lilToonInspector.RunBake`(기본 RenderTexture → ReadPixels)와 동일하게 재현. 셰이더가 없으면(=lilToon 미설치) 로그만 남기고 건너뛴다. lilToon 이 베이커 셰이더의 프로퍼티 이름을 바꾸면 여기서 깨지므로 lilToon 업데이트 후 색 굽기 결과를 한 번 확인할 것(개발 기준 lilToon 2.3.2).
- 원본 텍스처는 덮어쓰지 않음(`<텍스처>_Baked.png` 신규, 임포트 설정 복사, PNG/JPG 는 원본 파일 바이트를 직접 읽어 임포트 크기·압축 영향 배제). 같은 텍스처+같은 설정은 결과 공유. 텍스처 없는 머티리얼은 4×4 단색 텍스처. 실패 시 해당 머티리얼 무변경. `YAMO_MaxMainTextureMap.tsv` 행 갱신/추가. 메인 레이어만 굽는다(2nd/3rd·데칼 제외).

- **이미 NiloToon 이면 무동작**: `IsAlreadyNiloToon(root)`(참조 `.mat` 전부 NiloToon_Character). `ConvertAvatar` 는 이 경우 백업·변환·정리 없이 `AlreadyNiloToon=true` 로 반환하고, UI 는 안내 + Convert 버튼 비활성. 혼합 아바타에서는 **이번에 새로 변환된 머티리얼만 정리**하고 원래 NiloToon 이던 것은 `UntouchedNilo` 로 남긴다(손으로 조정한 Smoothness 등을 초기화하지 않기 위함). 이 보호를 없애지 말 것 — 전체 정리는 `CleanupOnly` 로 명시 실행.
- **버전 게이트**: `MinimumNiloToonVersion = "0.17.14"`(개발·검증 버전). NiloToon `package.json`의 version 을 읽어(`PackageInfo.FindForAssembly` → 실패 시 asmdef 위치에서 상위로 탐색) 미만/미설치/확인 불가면 `IsSupported == false` → 윈도우가 섹션 4 를 **아예 그리지 않는다**. NiloToon 쪽 API·프로퍼티 이름이 바뀌어 최소 버전을 올려야 하면 이 상수만 수정.
- **백업은 이동 가능**: 맵 파일(`_YAMO_MaterialBackupMap.tsv`, v2)에 원본 GUID·백업 GUID·백업 파일명·당시 원본 경로를 기록. 백업 폴더 생성 시 그 안에 TSV 를 남기고, **복구는 사용자가 백업 폴더를 선택 → 그 폴더의 TSV 를 읽는 방식**(`LoadBackupSet`; 자동 검색 없음, UI 는 폴더 경로가 바뀔 때만 TSV 를 다시 읽도록 캐시). 백업 머티리얼은 선택한 폴더 안의 파일명 우선(폴더를 복제해 GUID 가 달라져도 동작) → 백업 GUID, 원본 머티리얼은 GUID 우선 → 기록된 경로. 백업 위치를 경로로 하드코딩하는 코드를 다시 넣지 말 것.
- NiloToon 은 **리플렉션으로만** 접근(`NiloToon.NiloToonURP.NiloToonEditorPerCharacterRenderControllerCustomEditor.AutoSetupCharacterGameObject(GameObject,bool,bool)`) — asmdef 하드 참조/define 불필요. 미설치 시 UI 에 경고만 표시.
- 정리 대상은 `BuildFeatures()` 의 데이터 테이블(토글 프로퍼티 + 키워드 + 판정 텍스처). NiloToon 은 LWGUI `[Main(group, KEYWORD)]` 구조라 **토글 float 과 키워드를 반드시 함께** 꺼야 한다. 기능을 추가/제외하려면 이 테이블만 수정. `Feature.KeepIfColorNotBlack`(현재 Emission=`_EmissionColor`)이 지정된 기능은 텍스처가 없어도 그 색이 검정이 아니면 유지한다 — 맵 없이 색만으로 발광시키는 정상 사용을 끄지 않기 위함(사용자 확정).
- Smoothness 그룹은 끌 수 없어 `Shader.GetPropertyDefault*Value` 로 셰이더 선언 기본값을 읽어 초기화(하드코딩 아님).

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
| 9. Magica Collider Symmetry Fixer | Biped 변환 아바타에서 MagicaCloth2 콜라이더의 Automatic 시메트리가 반대편 본을 못 찾는 문제 수정 — 팔/다리 Primary 본 아래 콜라이더 중 Symmetry Target 이 비었거나 Biped 본인 것을 찾아 `X_Symmetry` + 반대편 Primary 본으로 설정(SerializedObject 접근, MagicaCloth2 하드 참조 없음). API: `ScanMagicaSymmetryTargets`, `FixMagicaSymmetryTargets` |
| 10. Boneless SMR Fixer | bones/bindposes 가 0개인 SMR(블렌드셰이프만 있는 소품 등 — FBX/VRM 익스포트 시 메시 소실) 검출. FIX: SMR 과 같은 부모·같은 로컬 트랜스폼에 `<이름>_Bone` 생성 + 100% 스키닝한 메시 사본(`<메시>_Skinned.asset`, 원본 메시 옆)으로 교체. API: `ScanBonelessSmrsInScene/Children`, `FixBonelessSmrs` |

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
| Avatar Bake & Prefab | Avatar Bake / Biped Converter / Biped Deconverter / Scale Bake | 1~4번 하위 탭, 선택 도구만 DrawGUI() 임베드 |
| Material & Texture | `MaterialAndTextureCollectorWindow` | DrawGUI() 임베드 |
| Asset Checker | `YamoAssetChecker` | DrawGUI() 임베드 |
| Animation | FacialAnimationBaker, ForearmHingeBaker, AnimClipReducerWindow | Launcher (별도 창 열기) — UI Toolkit 기반 도구 포함이라 IMGUI 임베드 곤란 |

- **Avatar Bake 하위 탭**은 내용의 스크롤 바깥에 고정된다. 각 도구는 입력값과 스크롤 위치를 별도로 유지하고, Converter/Deconverter는 결과뿐 아니라 전체 내용을 스크롤한다.
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
- **Pipeline Options**: Validate Unique Names, Zero BlendShapes Before Bake, Restore Source After Bake, Update When Offscreen (Prefab)
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
7.5) Auto/Humanoid + Bip001 본이 있으면 동일 베이크 결과에서 본만 추출한 _Avatar.fbx 생성
8) (옵션) Restore source — snapshot 기준 lockstep 으로 active state + BlendShape weight 되돌림
9) Import + AvatarBakeFbxSetup — Read/Write + Legacy Blend Shape Normals ON, 원본 머티리얼 매핑
   Bip001 Avatar가 있으면 본체 FBX는 Copy From Other Avatar, 프리팹 Animator는 해당 Avatar를 직접 참조
10) Instantiate FBX → targetInstance 씬 배치
11) BuildBoneMap(snapshot → targetInstance)
12) Migrate (snapshot 을 source-of-truth):
    - Active States  ← MigrateActiveStates
    - BlendShape weights  ← MigrateBlendShapes
    - Physics  ← MigrateColliders + MigrateMagicaCloth + MigrateVRMSpringBone
    - Constraints  ← MigrateConstraints
    - NiloToonPerCharacterRenderController (원본에 있을 때만) ← 설정·활성화 상태·내부 참조 복사
13) (옵션) updateWhenOffscreen = true 일괄 적용
14) PrefabUtility.SaveAsPrefabAsset (덮어쓰기)
```

진행률은 `EditorUtility.DisplayProgressBar` 로 표시. 성공 시 snapshot/targetInstance는 정리하고, 실패 시 진단용으로 남긴다. Restore Source After Bake가 켜져 있으면 실패 경로에서도 원본 활성화 상태와 BlendShape 가중치를 복원한다.

#### `Biped/BakeExport/AvatarScaleBakeUtility.cs` — 4. Scale Bake
- 씬의 프리팹 인스턴스 루트에 양수·균일 배율을 적용한 뒤 지정한다. 예: Scale (0.9, 0.9, 0.9).
- 연결된 원본 프리팹 폴더에 `원본이름_scale_0.9.fbx`와 `.prefab`을 저장한다. 씬에서 바꾼 이름 대신 실제 프리팹 파일명을 사용하며 Variant도 지원한다.
- 임시 복사본에서 전체 파이프라인을 실행한다. 루트 localScale만 사용하므로 부모의 배율이나 씬 배치가 결과에 섞이지 않으며, 원본은 유지되고 결과 Scale은 (1, 1, 1)이 된다.
- 숨김 오브젝트 명세도 `원본이름_scale_0.9_HiddenObjects.md`로 분리한다. 기존 FBX/프리팹/Avatar FBX를 덮어쓸 때는 경로를 보여주고 확인한다.

#### `Biped/BakeExport/AvatarBakeFbxSetup.cs`
- 모든 출력 FBX의 Read/Write와 Legacy Blend Shape Normals를 활성화한다. Unity 2021의 Legacy 옵션은 SerializedObject로 접근한다.
- 본체 FBX는 원본 렌더러/슬롯에 연결된 영구 Material 자산을 AddRemap으로 매핑한다. 슬롯 수 불일치나 모호한 매핑은 오류로 중단한다. 이전 Material Import: None 옵션은 제거했다.
- Auto/Humanoid 모드에서 Bip001 본이 있으면 `이름_Avatar.fbx`를 생성한다. Bip001 이름의 본과 필요한 부모 Transform만 복사하며, 메시/렌더러/기타 컴포넌트는 포함하지 않는다.
- 원본 Humanoid의 Bip001 매핑을 우선 보존하고 이름 매핑으로 보완한다. 필수 본과 생성된 Avatar의 유효성을 검사하며, 본체 FBX와 프리팹에서 이 Avatar를 사용한다. Generic 모드나 Bip001이 없는 아바타는 기존 단일 FBX 흐름을 유지한다.
- 일부 입력 메시의 smoothing group이 없으면 Legacy Blend Shape Normals 계산 경고가 발생할 수 있다.

#### `Biped/BakeExport/AvatarBakeComponentMigration.cs`
- 원본 루트/자식의 NiloToonPerCharacterRenderController만 조건부 복사한다. NiloToon 어셈블리 하드 참조를 추가하지 않는다.
- 직렬화된 설정과 enabled 상태를 복사하고, Head/Bounds/Renderer 등 내부 참조를 대상 계층으로 재연결한다. 외부 자산 참조는 유지한다.
- 대상에 없는 내부 컴포넌트 참조는 임시 snapshot을 가리키지 않도록 비우고 경고한다. 반복 호출 시 컴포넌트를 중복 추가하지 않는다.

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
| 머티리얼 매핑 오류로 베이크가 중단됨 | 원본 Material을 자산으로 저장했는지, 렌더러/슬롯이 일치하는지 확인. 0.10.0부터 자동 매핑하며 모호한 연결은 중단 |
| Secondary 의 VRMSpringBone 이 1 개로 줄어듦 | 해결됨. `MigrateVRMSpringBone` 가 `AddComponent` 로 항상 새로 추가, `clearedDsts` 로 재실행 누적만 방지 |
| Spine→Neck 본이 5 개인데 휴머노이드 리네임이 안 됨 | 의도된 abort. 본 계층 정리 후 재시도. 정상 chain intermediates ≤ 2 |
| Hub 단축키 충돌 | `Edit ▸ Shortcuts` → "YAMO/Open Tool Hub" 검색 → 다른 키로 변경 |

## 9. 변경 이력 요약

### 0.10.0
- Avatar Bake & Prefab를 1~4번 하위 탭으로 구성하고, 4번 Scale Bake 추가.
- 루트 배율을 메시/본에 베이크해 원본 프리팹 옆에 배율 이름으로 저장. 원본과 배율별 숨김 명세 보존.
- Read/Write와 Legacy Blend Shape Normals 자동 활성화, 원본 머티리얼 자동 매핑.
- Bip001 전용 `_Avatar.fbx`에서 Humanoid Avatar 생성 후 본체 FBX와 프리팹에 연결.
- 원본 NiloToon 컨트롤러의 설정·활성화 상태·내부 참조를 생성 프리팹에 이관.

### 0.9.0
- Asset Checker `10. Boneless SMR Fixer` 추가 — bones/bindposes 가 0개인 SkinnedMeshRenderer(FBX/VRM 익스포트 시 메시 소실)를 검출하고, 같은 부모·같은 로컬 트랜스폼에 `<이름>_Bone` 을 만들어 100% 스키닝.
- Asset Checker `9. Magica Collider Symmetry Fixer` 추가 — Biped 아바타의 MagicaCloth2 콜라이더 Symmetry Target 을 반대편 Primary 본으로 지정.
- Material And Texture Tool 섹션 4 를 `Convert NiloToon` 으로 교체(기존 "닐로툰 매트캡 자동 인식기" 제거) — 머티리얼 백업(폴더 안 TSV, 폴더 선택식 복구) → lilToon 메인 컬러/HSV·Gamma/그라디언트 텍스처 굽기 → NiloToon Auto setup → 텍스처 없는 활성 기능 정리. NiloToon 0.17.14 이상에서만 노출, 이미 NiloToon 인 아바타는 무동작.
- 머티리얼 복사가 현재 셰이더에 없는 프로퍼티의 잔여 텍스처 참조도 복사·재지정(`CopyStaleTextureReferences`), `CollectDuplicates` 의 비텍스처 프로퍼티 에러 로그 제거.

### 0.8.0
- Added BlendShape Curve Remapper with Mesh-path/property selection, configurable value mapping, Linear tangents, and EditMode coverage.
- Updated the Miltina Biped template FBX and its Humanoid avatar mapping/import settings.

### 0.5.7
- BlendShapeLookAt: `headForwardLocal` 기본값 `(0,1,0)`, `headUpLocal` 기본값 `(-1,0,0)` 으로 변경

### 0.5.6
- YamoCam Follow: `positionOffset` 및 축 마스킹(X/Y/Z)을 타겟 로컬 공간 기준으로 변경 → 타겟 루트 회전 시에도 카메라 상대 위치 유지
- `MainCameraScreenshotCapture` 추가 (`Editor/Camera/`) — 에디트 모드 Main Camera PNG 즉시 저장, Scene View Overlay 버튼
- BipedConverter FBX 템플릿 파일 추가 (`Editor/BipedConverter/Templates/`)

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
