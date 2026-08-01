# Mocap to Biped FBX Pipeline

`Tools/YAMO/Animation/Mocap to Biped FBX Pipeline`은 다음 작업을 순서대로 실행합니다.

1. FBX 입력이면 변경 전 원본을 `원본파일명_Backup.fbx`로 보존하고 OptiTrack 모션을 바인딩
2. Anim 입력이면 백업과 OptiTrack 바인딩 없이 해당 AnimationClip을 직접 사용
3. 지정한 Biped Animator에서 Forearm Hinge를 선택한 모드로 메모리상 베이크
4. 최종 출력용 Biped 복제본의 Humanoid Avatar를 제거하고 Generic Transform 클립으로 전환
5. 같은 Sample Rate로 Biped 전체 계층을 다시 샘플링해 FBX 생성
6. 자식 본 로컬 축을 보존하면서 3ds Max Z-up, right-handed, centimeter FBX로 변환
7. 결과 FBX를 다시 열어 축과 단위를 검증하고 임시 Hinge 클립 폐기

최종 FBX는 Maya 호환 이름 치환을 사용하지 않으므로 Biped 본 이름의 공백을 `_`로 바꾸지 않고 그대로 보존합니다.

기본 Sample Rate는 전 단계 공통 60fps입니다.

## 사용법

1. 씬의 Humanoid Biped Animator를 지정합니다.
2. OptiTrack FBX 또는 Anim 파일을 큐에 드래그합니다. 프로젝트 폴더를 추가하면 내부 모션을 일괄 등록할 수 있습니다.
3. 최종 FBX 폴더를 지정합니다.
4. `전체 파이프라인 실행`을 누릅니다.

길이를 0으로 두면 입력 클립 전체를 사용합니다. 시작 시간과 길이를 입력하면 최종 FBX만 해당 구간으로 잘립니다. Hinge 결과는 메모리에서만 사용되며 별도의 `.anim` 파일을 만들지 않습니다.

## 충돌 정책

- `Fail`: 같은 이름의 Motion 또는 `_T.fbx`가 있으면 해당 항목을 중단합니다. 기본값이며 기존 에셋을 보호합니다.
- `Overwrite`: 기존 FBX 애니메이션 설정 도구와 같은 방식으로 기존 Motion 및 `_T.fbx`를 교체합니다.
- 최초 백업 경로는 소스 Importer에 기록됩니다. 바인딩으로 원본 이름이 바뀌거나 파이프라인을 다시 실행해도 최초 `_Backup.fbx`를 재사용하며 덮어쓰지 않습니다.
- `_T.fbx`는 Motion Avatar가 참조하므로 성공 후 삭제하면 안 됩니다.
- 최종 FBX 덮어쓰기는 기본적으로 타임스탬프 `.bak` 파일을 만듭니다.

기본값은 **Play Mode Bake**입니다. 실제 Animator를 실행하므로 Foot IK와 런타임 Foot Stabilization이 반영됩니다. 빠른 처리가 필요하거나 런타임 안정화가 필요하지 않으면 `Hinge Bake Mode`를 **Edit Mode**로 전환할 수 있습니다. 배치 큐는 한 번의 Play Mode 진입에서 순서대로 기록되며 Edit Mode 복귀 후 최종 FBX Export가 자동으로 이어집니다.

Humanoid Avatar는 Hinge Bake까지 유지되며, 그 뒤 최종 FBX용 임시 복제본에서만 제거됩니다. 씬의 원본 Biped Animator와 Avatar는 변경하지 않습니다.
