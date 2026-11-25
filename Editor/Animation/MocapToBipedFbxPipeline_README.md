# Mocap to Biped FBX Pipeline

`Tools/YAMO/Animation/Mocap to Biped FBX Pipeline`(단축키 `8`)은 브라우저처럼 상단 탭으로 나뉩니다.

| 탭 | 용도 |
|---|---|
| **OptiTrack 파이프라인** | OptiTrack(Motive) FBX 큐 → Raw 손 보존 → MAX·FBX |
| **MMRP 파이프라인** | MMRP(Mingle Motion Replayer) FBX 큐 → 같은 팔 보존 파이프라인 |
| **FBX 애니메이션 설정** | 클립 임포트 설정 일괄 적용, 바인딩만 실행 (예전 `FBX 애니메이션 설정` 창이 통합됨, [FbxAnimSetup_README](FbxAnimSetup_README.md)) |

두 파이프라인 탭은 **모션 큐를 따로** 가지며, 대상 Biped·출력 폴더·처리 설정은 공유합니다.
실행 버튼은 현재 탭의 큐만 처리합니다.

## Raw 손 보존과 MAX 재처리

**OptiTrack·MMRP 양쪽 탭에서 `Raw 손 보존 + MAX 재처리`를 사용합니다.**
이 옵션은 새 창에서 기본으로 켜지며, 끄면 기존 Play/Edit Mode 베이크 설정을 사용할 수 있습니다.
API의 `MocapPipelineSettings.ArmChain.Enabled`는 기존 호출 호환성을 위해 기본 `false`입니다.

1. 같은 캐릭터의 대상 Biped Animator와 FBX 출력 폴더를 지정합니다.
2. 원본 기준 `.max`, **3ds Max 2025의 `3dsmaxbatch.exe`**, MAX 출력 폴더를 지정합니다.
3. 원본 T포즈 프레임, Biped 이름(기본 `Bip001`), 추출 Selection Set(기본 `Export_Biped`)을 확인하고 실행합니다.

기준 MAX는 매 항목마다 새로 열며, 파일 자체를 저장하거나 덮어쓰지 않습니다.
작업 중인 대화형 Max 창에는 연결하지 않고, 별도의 숨김 배치 프로세스를 사용합니다.
기준 MAX의 **Separate Arms Tracks는 꺼져 있어야** 하며 Unity Biped와 본 이름·팔 비율이 일치해야 합니다.
단일 Take/클립의 Raw FBX를 지원합니다. `.anim` 직접 입력은 기존 베이크 모드를 사용하세요.

### 처리 방법

- 원본 FBX와 `.meta`는 유지하고, 작업 복사본에서 기존 소스별 Humanoid 바인딩을 수행합니다.
- 선택 구간과 T포즈 기준 구간만 Edit Mode에서 베이크합니다. ForearmHinge 보정은 적용하지 않습니다.
- Raw 어깨→손 벡터를 대상 팔 길이 비율로 조정하고, T포즈 축 차이를 보정한 손 월드 회전을 목표로 사용합니다.
- 거의 펴진 팔에서 평면이 뒤집히는 경우만 앞뒤 4프레임 안에서 연결합니다. 손 목표는 바꾸지 않습니다.
  큰 원본 불연속은 자동으로 매끈하게 만들지 않고 검증 기록에 남깁니다.
- Biped 네이티브 회전 키를 쓴 뒤 MAX를 저장·재로드하고, 기록 프레임의 손 목표와 다른 본의 로컬 포즈 보존을 검사합니다.
- Selection Set의 본 포즈를 매 프레임 기록하여 FK FBX로 추출합니다. 손목의 XYZ/ZYX 순서를 비교해
  표본 중간 프레임의 최대 오차와 95백분위가 악화되지 않는 쪽을 선택합니다.
- FBX를 다시 읽어 팔 체인의 기록 프레임 포즈와 **1프레임 키 간격**을 검사합니다.
- Assets 안의 최종 출력은 지정 Avatar로 실제 재생하여 전 기록 프레임의 손 위치·회전과 상완·전완 오차까지 검사하고, 통과한 결과만 반영합니다.

MAX의 **0f에는 기준 T포즈**를 보관하고 실제 모션 재생 범위는 **1f부터** 설정합니다.
FBX는 이 기준 프레임을 제외하여 **0f부터** 출력하므로 선택한 모션의 시작 시점과 길이는 유지됩니다.

대상 Avatar의 팔 Twist 프리셋은 **Upper Arm 1 / Lower Arm 0**입니다.
자동 적용 옵션은 대상 FBX Avatar의 두 필드만 변경하고 기존 `.meta`를 백업합니다.
나머지 muscle min/max는 유지합니다. Twist는 각도 제한이 아닌 회전 분배 비율이며,
이 프리셋은 검증한 Biped 구조를 기준으로 합니다.

### 결과와 검증 기록

| 파일 | 위치 |
|---|---|
| Generic FBX | FBX 출력 폴더 / 모션명 / `모션명.fbx` |
| 지정 Avatar를 적용한 Humanoid FBX | FBX 출력 폴더 / 모션명 / `Ani_모션명.fbx` |
| 편집 가능한 네이티브 Biped MAX | MAX 출력 폴더 / 모션명 / `Ani_모션명.max` |
| 원본·덮어쓰기 백업, 포즈 캐시, 로그, `job.json`, 검증 `result.json`·`unity-replay.json` | 프로젝트 `Codex/YYYY-MM-DD/MocapArmChain/실행ID/` |
| 바인딩용 Unity 입력 복사본과 `_T` | `Assets/Codex/YYYY-MM-DD/MocapArmChain/실행ID/` |

FBX 출력이 현재 프로젝트의 Assets 안에 있을 때만 Unity의 Generic/Humanoid 설정과 Avatar를 자동 적용합니다.
Assets 밖 FBX는 검증된 파일만 저장하며, 다른 Unity 프로젝트로 가져갈 때 Avatar를 지정해야 합니다.
MAX 출력은 Assets 밖을 사용합니다. 최종 에셋은 작업용 `Codex`의 파일을 참조하지 않습니다.
기존 FBX를 교체할 때 GUID·클립 내부 ID·클립 이름을 보존하며, 반영 중 오류가 발생하면 기존 출력과 importer를 복구합니다.
작업용 검증·백업 파일을 작업 완료만을 이유로 일괄 삭제하지 않습니다.

각 항목은 샘플을 최대 50,001개까지 처리합니다. 선택한 구간의 클립과 복제본을 해제한 뒤 MAX를 실행하고,
다음 항목에는 새 Max 프로세스를 사용합니다. 취소는 단계 경계와 MAX 처리 대기 중에 지원합니다.
Unity FBX Exporter 자체가 실행되는 동안은 그 호출이 끝나야 취소할 수 있습니다.

기록 프레임이 일치하더라도 Max TCB·FBX Euler·Humanoid muscle의 프레임 사이 보간은 완전히 같지 않습니다.
쇄골의 Humanoid 재구성 차이도 남을 수 있습니다. `result.json`의 오차와 원본 불연속 기록을 함께 확인하세요.
`unity-replay.json`은 몸통 기준 좌표에서 Generic→Humanoid 손 오차(최대 0.5mm/1도 미만), 상완·전완 회전 95백분위(5도 미만)를 검사합니다. 쇄골 오차는 별도로 보고합니다.
MAX 파일에는 이 도구의 커스텀 컨트롤러를 저장하지 않으며, 동봉한 네이티브 모듈은 배치 실행에만 사용합니다.
현재 자동화 지원 버전은 **3ds Max 2025**입니다. 다른 버전에는 해당 SDK를 사용한 별도 포팅·검증이 필요합니다.
네이티브 소스와 Python 실행 코드는 [`Tools~/ArmChain`](../../Tools~/ArmChain)에 포함됩니다.

아래의 Forearm Hinge/Play Mode 설명은 `Raw 손 보존 + MAX 재처리`를 끈 기존 베이크 모드에 해당합니다.

## 베이크 대상 임시 Avatar

OptiTrack/MMRP의 Edit Mode·Play Mode 베이크는 지정된 캐릭터의 **FBX 기반 Avatar**로부터 실행별 본 전용 FBX를 추출합니다.
원본 FBX/Avatar/씬 캐릭터의 설정은 바꾸지 않습니다. 원본 human 매핑과 muscle 범위, Upper Arm Twist 등은 유지하고,
**임시 대상 Avatar의 Lower Arm Twist만 1**로 설정하여 Hand 쪽으로 비틀림을 배분합니다.
모션 입력의 `_T` Avatar 설정과는 별개입니다.

임시 FBX에는 스켈레톤·매핑 본·스킨 본과 필요한 조상 Transform만 남기며 메시/재질/카메라/라이트/애니메이션은 제외합니다.
원본과 같은 좌표/스케일 임포트 설정으로 읽은 뒤, Avatar Configuration의 **Pose > Reset**과 같은
`AvatarSetupTool.CopyPose → TransferPoseToDescription`을 수행하여 포즈를 저장합니다.
Enforce T-Pose나 Biped Pose로 대체하지 않으며 Reset API를 찾지 못하면 작업을 중단합니다.
매핑 일치, 유효한 Humanoid, Lower Arm Twist 1, 메시 부재를 검사한 다음 베이크합니다.

임시 대상은 `Assets/Codex/YYYY-MM-DD/MocapTarget/실행ID/Target.fbx`에 생성합니다.
Edit Mode는 전체 큐의 종료/오류/취소 시, Play Mode는 Edit Mode 복귀 후 최종 FBX 출력이 끝나면
해당 실행의 인스턴스와 FBX만 자동 삭제합니다. Editor 강제 종료 시에는 정리가 실행되지 않을 수 있습니다.
최종 출력은 Generic Transform 애니메이션이므로 임시 Avatar를 참조하지 않습니다.
본 전용 타깃에서는 메시/블렌드셰이프를 출력하지 않으며 원본 캐릭터의 런타임 컴포넌트도 복제하지 않습니다.
기존 Forearm Hinge/Hand Compensation 옵션은 별도 후처리로 유지됩니다.

Play Mode 컨트롤러는 `Assets/Codex/YYYY-MM-DD/MocapPipeline/실행ID/`,
기록 파일은 `Codex/YYYY-MM-DD/MocapPipeline/실행ID/`를 사용합니다.

## 소스 형식

| 형식 | 본 이름 | 출력 이름 근거 | 비고 |
|---|---|---|---|
| OptiTrack | `001_Hips`, `001_Spine1`… (액터 접두사) | FBX 테이크 이름 | 스파인 자동 매핑이 한 칸 밀려 `Spine/Chest`를 강제 지정 |
| MMRP | `Hips`, `LeftUpperArm`, `LeftHandIndex1`… (Unity 표준 이름) | 파일 이름 (테이크는 항상 `Take 001`) | 바인드 포즈가 이미 T포즈, 표준 이름을 그대로 매핑 |

두 형식 모두 `LeftEye / RightEye / Jaw / UpperChest` 매핑은 제거합니다. MMRP의 `MMRP_*` 헬퍼 노드는 절대 매핑하지 않습니다.

OptiTrack의 복사된 `_T.fbx`는 `Spine → 접두사_Spine1`, `Chest → 접두사_Spine3`으로 설정합니다.
그 다음 Avatar Configuration의 **Enforce T-Pose** 보정을 실행하고, 보정된 skeleton을 **Apply(재임포트)** 한 뒤에만 모션 FBX에 아바타를 연결해 리타게팅합니다. T포즈 보정에 실패하면 바인딩을 중단합니다.

큐에 넣을 때 FBX의 본 이름으로 형식을 판별해 **다른 탭의 파일은 큐에 들어가지 않고** 어느 탭을 써야 하는지 알림으로 표시합니다. 판별을 통과하지 못한 파일이 실행 단계까지 오면 `_Backup`이나 `_T`를 만들기 전에 거부됩니다.

각 파이프라인 탭은 다음 작업을 순서대로 실행합니다.

1. FBX 입력이면 변경 전 원본을 `원본파일명_Backup.fbx`로 보존하고 탭 형식에 맞게 모션을 바인딩
2. Anim 입력이면 백업과 바인딩 없이 해당 AnimationClip을 직접 사용
3. 지정한 Biped Animator에서 Forearm Hinge를 선택한 모드로 메모리상 베이크
4. 최종 출력용 Biped 복제본의 Humanoid Avatar를 제거하고 Generic Transform 클립으로 전환
5. 같은 Sample Rate로 Biped 전체 계층을 다시 샘플링해 FBX 생성
6. 자식 본 로컬 축을 보존하면서 3ds Max Z-up, right-handed, centimeter FBX로 변환
7. 결과 FBX를 다시 열어 축과 단위를 검증하고 임시 Hinge 클립 폐기

최종 FBX는 Maya 호환 이름 치환을 사용하지 않으므로 Biped 본 이름의 공백을 `_`로 바꾸지 않고 그대로 보존합니다.

기본 Sample Rate는 전 단계 공통 60fps입니다.

## 사용법

1. 소스 형식에 맞는 탭(OptiTrack 또는 MMRP)을 선택합니다.
2. 씬의 Humanoid Biped Animator를 지정합니다.
3. FBX 또는 Anim 파일을 큐에 드래그합니다. 프로젝트 폴더를 추가하면 내부 모션을 일괄 등록할 수 있습니다.
   폴더 추가는 이 도구가 만든 `_T`, `_Backup` 파일을 항상 제외합니다.
4. 최종 FBX 폴더를 지정합니다.
5. `파이프라인 실행`을 누릅니다.

길이를 0으로 두면 입력 클립 전체를 사용합니다. 시작 시간과 길이를 입력하면 최종 FBX만 해당 구간으로 잘립니다. Hinge 결과는 메모리에서만 사용되며 별도의 `.anim` 파일을 만들지 않습니다.

## 이름 충돌 처리 (액터별 분리 파일, OptiTrack)

OptiTrack 바인딩 단계의 결과 파일명은 파일 이름이 아니라 **FBX 내부의 클립(테이크) 이름**에서
나옵니다. (MMRP는 파일 이름을 그대로 쓰므로 한 폴더 안에서 충돌하지 않습니다.) 그래서 같은 테이크를 액터별로 뽑은 파일들(`001.fbx`, `002.fbx` …)은
클립 이름이 모두 같아 하나의 목표 파일명을 두고 충돌합니다.

큐는 **실행 전에 전체 항목의 목표 이름을 미리 계산**하고, 겹치는 항목에만 원본
파일명(액터 번호)을 접미사로 붙여 모두 살립니다.

| 원본 | 클립(테이크) 이름 | 바인딩 결과 | 최종 FBX |
|---|---|---|---|
| `001.fbx` | `드립_003_FBX` | `드립_003_001.fbx` | `드립_003_001.fbx` |
| `002.fbx` | `드립_003_FBX` | `드립_003_002.fbx` | `드립_003_002.fbx` |
| `003.fbx` | `솔로_007_FBX` | `솔로_007.fbx` | `솔로_007.fbx` |

Play Mode Bake는 큐 전체를 **먼저 바인딩해 두고** 한 번의 Play Mode 진입에서
순서대로 기록하므로, 이름 충돌이 나면 뒤 항목이 앞 항목의 모션 에셋을 지워
이미 준비된 클립 참조가 끊깁니다. 사전 계획은 이 경우를 막습니다.

최종 FBX 이름이 겹칠 때(입력이 `.anim`이거나 `Output Name`을 직접 같게 지정한
경우)도 `_2` 대신 원본 파일명을 먼저 붙여 어떤 캡처에서 나왔는지 남깁니다.

## 충돌 정책

- `Fail`: 같은 이름의 Motion 또는 `_T.fbx`가 있으면 해당 항목을 중단합니다. 기본값이며 기존 에셋을 보호합니다.
- `Overwrite`: 기존 Motion 및 `_T.fbx`를 교체합니다. 교체할 때는 반드시 로그에 사유를 남깁니다.
- `Disambiguate`: 목표 이름이 다른 에셋의 것이면 삭제 대신 `_2`, `_3` … 을 붙입니다. 자기 자신의 이전 결과물만 교체합니다.
- 최초 백업 경로는 소스 Importer에 기록됩니다. 바인딩으로 원본 이름이 바뀌거나 파이프라인을 다시 실행해도 최초 `_Backup.fbx`를 재사용하며 덮어쓰지 않습니다.
- `_T.fbx`는 Motion Avatar가 참조하므로 성공 후 삭제하면 안 됩니다.
- 최종 FBX 덮어쓰기는 기본적으로 타임스탬프 `.bak` 파일을 만듭니다.

기본값은 **Play Mode Bake**입니다. 실제 Animator를 실행하므로 Foot IK와 런타임 Foot Stabilization이 반영됩니다. 빠른 처리가 필요하거나 런타임 안정화가 필요하지 않으면 `Hinge Bake Mode`를 **Edit Mode**로 전환할 수 있습니다. 배치 큐는 한 번의 Play Mode 진입에서 순서대로 기록되며 Edit Mode 복귀 후 최종 FBX Export가 자동으로 이어집니다.

Humanoid Avatar는 Hinge Bake까지 유지되며, 그 뒤 최종 FBX용 임시 복제본에서만 제거됩니다. 씬의 원본 Biped Animator와 Avatar는 변경하지 않습니다.
