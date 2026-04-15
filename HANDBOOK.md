# YAMO Unity Tools — 해설서

이 문서는 `com.yamo.unitytools` 패키지의 **전체 구조, 각 스크립트의 역할, 수정 지점, 확장 방법**을 AI/사람 모두 빠르게 파악할 수 있도록 정리한 참고 문서다. 다른 컴퓨터·다른 프로젝트에서 AI를 활용해 이 패키지를 수정할 때 이 파일 하나만 읽히면 전체 맥락을 잡을 수 있도록 작성되었다.

---

## 1. 패키지 한눈에 보기

| 항목 | 내용 |
|---|---|
| 이름 | `com.yamo.unitytools` |
| 버전 | 0.2.0 (StreamDeck 제거, 의존성 자동 감지 도입) |
| 대상 | Unity 2021.3 이상, Editor 전용 |
| 어셈블리 | `YAMO.UnityTools.Editor` (코어, 외부 참조 없음) + `YAMO.UnityTools.Physics.Editor` (MagicaCloth2·VRM 강타입 참조, define 조건부) |
| 네임스페이스 | `YAMO.UnityTools.Editor` (모든 공개 클래스) / `YAMO.UnityTools.Editor.Internal` (내부) |
| 메뉴 루트 | `Tools/YAMO/...` |

## 2. 폴더/파일 구조

```
Packages/com.yamo.unitytools/
├── package.json                          ← 패키지 메타 (버전·Unity 최소버전)
├── HANDBOOK.md                           ← 이 문서
├── YAMO.UnityTools.Editor.asmdef         ← 코어 asmdef, references 비어있음
└── Editor/
    ├── Internal/
    │   └── YamoDependencyDetector.cs     ← 외부 패키지 자동 감지 / define 주입
    ├── Bones/
    │   ├── FindMissingBones.cs
    │   ├── FindUnusedBones.cs
    │   └── HumanBoneRenamer.cs
    ├── Hierarchy/
    │   ├── HierarchySearchTools.cs
    │   └── ObjectNameModifier.cs
    ├── Assets/
    │   ├── MaterialAndTextureCollectorWindow.cs
    │   └── MissingScriptRemover.cs
    ├── Animation/
    │   └── FacialAnimationBaker.cs
    ├── Physics/
    │   ├── YAMO.UnityTools.Physics.Editor.asmdef  ← 별도 asmdef (defineConstraints 로 조건부 컴파일)
    │   └── AvatarPhysicsMigrator.cs      ← MagicaCloth2 ↔ VRM SpringBone 마이그레이션 (강타입 참조)
    └── Layout/
        └── GlobalLayoutManager.cs
```

- **폴더 = 기능 카테고리**. asmdef는 루트 하나뿐이므로 폴더를 옮기거나 늘려도 컴파일 단위에는 영향 없음. 파일의 .meta만 같이 움직이면 GUID 유지되어 Unity가 레퍼런스를 잃지 않는다.
- `Internal/` 은 네임스페이스가 `YAMO.UnityTools.Editor.Internal` — 외부에서 쓸 일이 없는 인프라 코드만 들어간다.

## 3. 의존성 처리 — 이 패키지의 핵심 설계

### 3-1. 두 외부 패키지
- **MagicaCloth2** — `Assets/External/MagicaCloth2/` (수동 import). 어셈블리 이름 `MagicaClothV2`.
- **VRM** (UniVRM 0.x) — 설치 방식 다양 (UPM / .unitypackage / 수동). 어셈블리 이름 `VRM`.

### 3-2. 두 개의 asmdef — 코어와 조건부 분리

- **`YAMO.UnityTools.Editor.asmdef`** (코어) — `references: []`. 외부 패키지 참조 **없음**. 어떤 환경에서도 무조건 컴파일 성공.
- **`Editor/Physics/YAMO.UnityTools.Physics.Editor.asmdef`** — `references: ["YAMO.UnityTools.Editor", "MagicaClothV2", "VRM"]`. 강타입 API 사용을 위해 참조를 담.
  - `defineConstraints: ["YAMO_HAS_MAGICACLOTH", "YAMO_HAS_VRM"]` — 두 심볼이 모두 정의된 경우에만 이 asmdef가 컴파일됨. 둘 중 하나라도 없으면 **asmdef 전체가 비활성**되어 references가 깨져도 에러 안 남.
  - 이 덕분에 `AvatarPhysicsMigrator.cs` 안에 `#if` 가드가 필요 없음 — asmdef 레벨에서 이미 처리.

### 3-3. `YamoDependencyDetector` — 자동 감지 + define 주입

`Editor/Internal/YamoDependencyDetector.cs` 는 `[InitializeOnLoad]`로 Unity 로드/리컴파일 직후 실행:

1. `AppDomain.CurrentDomain.GetAssemblies()` 로 현재 로드된 어셈블리 목록을 스캔
2. 표에 정의된 쌍 `(어셈블리 이름, define 심볼)` 을 각각 확인
3. 존재 O + define X → define 추가 / 존재 X + define O → define 제거
4. 변경된 경우 `PlayerSettings.SetScriptingDefineSymbols` 로 기록 + 로그 1줄 출력

```
(MagicaClothV2, YAMO_HAS_MAGICACLOTH)
(VRM,           YAMO_HAS_VRM)
```

**다른 의존성을 추가하고 싶다면** `Detectors` 배열에 쌍을 하나 더 넣기만 하면 된다:
```csharp
("FinalIK", "YAMO_HAS_FINALIK"),
```

### 3-4. 각 스크립트의 가드 전략

| 파일 | 가드 방식 | 비고 |
|---|---|---|
| `AvatarPhysicsMigrator.cs` | **asmdef 레벨** (`defineConstraints`) | 파일 안에는 `#if` 없음. asmdef 전체가 조건부로 켜짐 |
| 나머지 전부 | 가드 없음 | `typeof(T).Name == "..."` 리플렉션 방식이라 의존성 없이 컴파일됨 |

→ **결과**: 아무것도 설치 안 된 환경에서도 YAMO 패키지는 무조건 컴파일 성공. MagicaCloth/VRM 특유 기능은 자동으로 비활성.

## 4. 각 스크립트 상세 해설

### 4-1. `Internal/YamoDependencyDetector.cs`
- `[InitializeOnLoad]` + `EditorApplication.delayCall` 로 초기화 타이밍 안전화.
- `Detectors` 배열이 "이 패키지는 이 define이 주입되어야 한다"의 단일 소스 오브 트루스.
- **수정 빈도**: 새 외부 패키지 통합 추가 시에만.

### 4-2. `Bones/FindMissingBones.cs` — `Tools/YAMO/Bones/Find Missing Bones`
- Scene 전체 또는 선택 GameObject 하위의 모든 `SkinnedMeshRenderer`를 조사해, `bones` 배열에 null이 있는지, `rootBone`이 null인지 체크.
- 결과는 창에 리스트로 표시 + `선택` 버튼으로 해당 오브젝트로 핑/이동.
- 외부 의존성 없음. Unity 기본 API만 사용.
- **수정 포인트**: `CheckRenderer()` — 판정 로직 추가 / `DrawResults()` — UI 커스터마이즈.

### 4-3. `Bones/FindUnusedBones.cs` — `Tools/YAMO/Bones/Find Unused Bones`
- 선택한 루트 하위에서, 어느 `SkinnedMeshRenderer.bones`에도 포함되지 않은 Transform을 "사용되지 않는 본"으로 판정해 Selection에 추가.
- 제외 옵션:
  - 문자열 포함 필터 (사용자가 + 버튼으로 추가)
  - MagicaCapsule/Sphere/Plane Collider 컴포넌트 유무 (타입명 문자열로 체크 → **의존성 없음**)
  - VRMSpringBone / VRMSpringBoneColliderGroup (같은 방식)
- **수정 포인트**: 새 필터 추가 시 `FindAndSelectUnusedBones()` 내부의 `typeName == "..."` 분기 추가.

### 4-4. `Bones/HumanBoneRenamer.cs` — `Tools/YAMO/Bones/Human Bone Renamer`
- Unity Humanoid Rig의 표준 본 이름(`Hips`, `LeftUpperArm` 등)으로 자동 rename.
- `humanBoneNames` dictionary: Unity의 `HumanBodyBones` enum → 표준 문자열 매핑 테이블.
- `bipedMapping` dictionary: 3ds Max Biped, Mixamo 등에서 흔한 본 이름 → `HumanBodyBones` 역매핑.
- **수정 포인트**: 새 rig 규격 대응 시 `bipedMapping`에 항목 추가.

### 4-5. `Hierarchy/HierarchySearchTools.cs` — `Tools/YAMO/Hierarchy/Search/...`
- Unity Hierarchy 검색창에 `t:타입` 필터를 리플렉션으로 직접 넣어주는 단축키 모음.
- Hierarchy 창은 `UnityEditor.SceneHierarchyWindow`(internal). `SetSearchFilter(string, ...)` 메서드의 시그니처가 버전마다 달라서 **파라미터 개수·타입을 런타임에 추론**해 맞는 값을 채운 뒤 invoke.
- **수정 포인트**: 새 필터 추가 시 `[MenuItem]` + `public static void XxxSearch() => SetHierarchySearch("t:YourType");` 한 줄만 추가.

### 4-6. `Hierarchy/ObjectNameModifier.cs` — `Tools/YAMO/Hierarchy/Object Name Modifier`
- prefix/suffix 일괄 부여, 중복 이름 감지 및 일괄 선택, localScale이 (1,1,1)이 아닌 Humanoid 본 감지.
- 루트 오브젝트를 받아 하위 전체 Transform에 작업.
- **수정 포인트**: `OnGUI()` 구성 / `FindInvalidScaleBones()` 판정 기준.

### 4-7. `Assets/MaterialAndTextureCollectorWindow.cs` — `Tools/YAMO/Assets/Material And Texture Tool`
- 가장 큰 파일 (1,288줄). 3개 섹션:
  1. **Material/Texture 수집·중복 해소**: Prefab 하위의 모든 머티리얼·텍스처를 지정 폴더로 복사/이동, 이름이 같은 중복 탐지.
  2. **PSD → PNG 변환**: `Assets` 하위 PSD 스캔, 정사각형 여부·크기 한계에 따라 일괄 PNG 변환.
  3. **기타 유틸**: foldout으로 묶여있음.
- 외부 의존성 없음. `AssetDatabase`, `TextureImporter` 기반.
- **수정 포인트**: 각 섹션이 private 메서드 뭉치로 분리돼 있음. `[MenuItem]` 하단의 섹션 foldout 플래그(`sec1Foldout` 등)가 UI 진입점.

### 4-8. `Assets/MissingScriptRemover.cs` — `Tools/YAMO/Assets/Missing Script Remover`
- 지정 GameObject 하위 전체에서 Missing Script 컴포넌트를 `GameObjectUtility.RemoveMonoBehavioursWithMissingScript`로 일괄 제거.
- **수정 포인트**: 거의 없음. UI가 단순.

### 4-9. `Animation/FacialAnimationBaker.cs` — `Tools/YAMO/Animation/Facial Animation Baker`
- BlendShape 키가 많아 YAML .anim 파일이 비대해진 페이셜 클립을, **캐릭터 독립적인 최소 셸(shell) 하이어라키**를 자동 생성한 뒤 Unity FBX Exporter(`com.unity.formats.fbx`)로 .fbx로 내보내 용량을 줄인다.
- 설계 포인트:
  - 클립의 `EditorCurveBinding` 에서 필요한 transform 경로·컴포넌트·블렌드셰이프 이름만 역추적해 임시 셸 GameObject 생성.
  - 임시 AnimatorController로 래핑해 FBX Exporter에 전달.
  - 결과 FBX의 경로 바인딩이 원본 .anim 그대로라 **어떤 캐릭터에 올려도 동일하게 적용됨**.
  - `try/finally` 로 임시 자산 및 Hierarchy 오브젝트 확실히 정리.
- FBX Exporter는 리플렉션으로 선택적 사용 — 없으면 에러 메시지만 띄우고 종료.
- **수정 포인트**: `keyframeStride` (키 샘플링 간격), `include` dictionary (내보낼 path 선택), 임시 셸 생성 로직 (`BuildShellForClip`류 메서드).

### 4-10. `Physics/AvatarPhysicsMigrator.cs` — `Tools/YAMO/Physics/Avatar Physics Migrator`
- 같은 폴더의 `YAMO.UnityTools.Physics.Editor.asmdef` 가 `defineConstraints: [YAMO_HAS_MAGICACLOTH, YAMO_HAS_VRM]` 로 조건부 컴파일을 담당. 두 심볼이 모두 있을 때만 이 asmdef(=파일)가 컴파일됨. 파일 내부 `#if` 가드 불필요.
- 소스 아바타(Armature, MagicaCloth·VRM 구성 있음) → 타깃 아바타(Biped 등 다른 rig)로 물리 세팅을 옮기는 마이그레이션 툴.
- 기능: Analyze(본 이름 매칭률·중복·컴포넌트 수 집계) / Migrate(MagicaCloth·VRMSpringBone·콜라이더 복사) / MagicaCloth PreBuild 자동 생성 / BlendShape 마이그레이션·리셋.
- **수정 포인트**: 맨 앞 `#if` 라인 / `Analyze()` / `Migrate()` / `AutoCreatePreBuildData()` / `MigrateBlendShapes()`.

### 4-11. `Layout/GlobalLayoutManager.cs` — `Tools/YAMO/Layout/Load ...`
- 저장된 Unity 창 레이아웃 (`.wlt`) 을 에디터 메뉴 한 번으로 즉시 적용.
- `UnityEditor.WindowLayout.LoadWindowLayout(string, ...)` (internal) 을 리플렉션으로 호출. 시그니처 변동에 대응해 파라미터 개수에 맞춰 인자 채움.
- `.wlt` 검색 경로:
  1. `Library/` 내부
  2. (Windows) `%APPDATA%/Unity/Editor-5.x/Preferences/Layouts/`
  3. (macOS) `~/Library/Preferences/Unity/Editor-5.x/Layouts/`
  4. 위 경로들의 `Default/` 하위
- **수정 포인트**: 새 레이아웃 추가 시 `[MenuItem]` + `Load_XXX() => LoadLayout("XXX");` 한 줄.

## 5. 공통 컨벤션 (유지할 것)

- **네임스페이스**: 모든 공개 클래스는 `YAMO.UnityTools.Editor`. 인프라/비공개는 `YAMO.UnityTools.Editor.Internal`.
- **MenuItem 경로**: `Tools/YAMO/<카테고리>/<기능명>`. 카테고리는 폴더명과 일치.
- **파일명 = 주 클래스명**. 파일에는 주 클래스 하나만 두는 것을 원칙으로.
- **외부 의존성 접근 원칙**:
  - **리플렉션 우선** (`typeof(T).Name == "..."`) — 가드 없이 안전.
  - 강타입 API가 꼭 필요할 때만 `#if YAMO_HAS_*` 로 감싸고, 그 파일에만 국한.
  - `using MagicaCloth2;` / `using VRM;` 같은 직접 import는 **반드시** 해당 `#if` 블록 내부에서만.
- **asmdef `references`는 비워둘 것**. 외부 의존성은 전부 `YamoDependencyDetector` 경유.

## 6. 자주 하는 확장 작업 템플릿

### 6-1. 새 메뉴 툴 추가 (외부 의존성 없음)
1. 적절한 카테고리 폴더(예: `Editor/Hierarchy/`)에 `NewTool.cs` 생성.
2. 템플릿:
   ```csharp
   using UnityEngine;
   using UnityEditor;

   namespace YAMO.UnityTools.Editor
   {
       public class NewTool : EditorWindow
       {
           [MenuItem("Tools/YAMO/Hierarchy/New Tool")]
           public static void ShowWindow() => GetWindow<NewTool>("New Tool");

           private void OnGUI() { /* ... */ }
       }
   }
   ```
3. 끝. asmdef 수정 불필요.

### 6-2. 새 외부 패키지 의존성 추가
1. `YamoDependencyDetector.cs` 의 `Detectors` 배열에 추가:
   ```csharp
   ("FinalIK", "YAMO_HAS_FINALIK"),
   ```
2. 해당 기능을 **별도 폴더 + 별도 asmdef** 로 분리 (Physics 폴더와 같은 패턴):
   ```
   Editor/FinalIK/
   ├── YAMO.UnityTools.FinalIK.Editor.asmdef  (references: [YAMO.UnityTools.Editor, FinalIK] / defineConstraints: [YAMO_HAS_FINALIK])
   └── YourFinalIKTool.cs
   ```
3. 파일 내에서는 `#if` 가드 없이 강타입 API 그대로 사용 가능 (asmdef가 조건부 컴파일 담당).
4. 사용자가 FinalIK를 프로젝트에 추가/제거하면 detector가 심볼을 토글하고 Unity가 자동 리컴파일.

### 6-3. 기존 카테고리 폴더 추가/변경
- 아무 파일이나 폴더로 옮기면 됨. `.meta` 파일만 함께 이동시킬 것 (GUID 유지). asmdef 수정 불필요.

## 7. 트러블슈팅

| 증상 | 원인 / 해결 |
|---|---|
| `The type or namespace 'MagicaCloth2' could not be found` | `YamoDependencyDetector`가 아직 동작 못 한 초기 상태. Unity 한 번 재컴파일 (Assets → Refresh) 후 해결 |
| `YAMO_HAS_MAGICACLOTH` 심볼이 안 생김 | 1) MagicaCloth2 asmdef 이름이 정말 `MagicaClothV2`인지 확인 2) `Detectors` 배열의 문자열 오타 확인 3) Player Settings → Scripting Define Symbols 직접 확인 |
| `AvatarPhysicsMigrator` 메뉴가 안 보임 | 두 패키지 중 하나라도 없으면 파일 전체가 가드되어 클래스가 존재하지 않음. 이게 **정상 동작**. |
| 파일을 옮겼더니 `.meta` 충돌 경고 | 파일과 `.meta`가 한 쌍으로 움직여야 함. 둘 다 같이 이동했는지 확인 |
| `CS0118: 'Editor' is a namespace but is used like a type` | 네임스페이스 `YAMO.UnityTools.Editor`의 마지막 세그먼트 `Editor`가 `UnityEditor.Editor` 클래스 참조를 가린 것. `typeof(Editor)` → `typeof(UnityEditor.Editor)` 로 풀네임 명시 |

## 8. 제거된 것들 (역사)

0.2.0에서 **Stream Deck 통합이 완전히 제거**되었다:
- `Editor/Streamdeck_Scripts/` 폴더 전체 삭제
- `YAMO.UnityTools_StreamDeck.asmdef` 삭제
- `YAMO_STREAMDECK` define 사용 중단
- `F10.StreamDeckIntegration` 네임스페이스의 모든 import·어트리뷰트 제거
- `ResetTransformEditor` (StreamDeck 전용 리셋 버튼) 삭제

외부 `Assets/External/StreamDeckIntegration/` 패키지 자체는 YAMO Unity Tools와 분리된 자산이므로 이 패키지 수정 범위가 아님 — 필요 없으면 수동으로 별도 제거 가능.

---

**이 문서 업데이트 원칙**: 스크립트를 추가·제거·이름 변경하면 §2(폴더 구조)와 §4(상세 해설)에 즉시 반영. 컨벤션이나 의존성 처리 방식이 바뀌면 §3, §5도 업데이트.
