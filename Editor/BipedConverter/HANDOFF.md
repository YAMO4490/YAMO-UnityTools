# Biped Converter — 스크립트 해설서

> `com.yamo.unitytools` 패키지의 BipedConverter 도구 전용 문서.
> 설계 의도, 동작 방식, 매핑 규칙, 결정 사항을 한 곳에 보존한다.

---

## 프로젝트 목표

Blender로 만들어진 **Humanoid 본 구조의 Armature 캐릭터**를 Unity 안에서 **3ds Max Biped 구조로 변환**하는 에디터 툴.

- 원본 본은 그대로 보존 (Mesh, SkinnedMeshRenderer, 시뮬레이션 데이터에 영향 0)
- 새로 만든 Biped 본 구조 하위에 원본 본을 reparent 하는 방식 ("뚜따")
- 후속 스크립트가 정확한 Biped 회전값을 받아서 사용해야 하므로, 본 이름과 회전값이 정확해야 함

---

## 현재 상태

마지막 검증 시점: Spine/Spine1 회전값과 위치 모두 sample 기준에 맞게 출력됨. 이후 멀티 템플릿, Spine1 수직 보정, 손가락 직선화, "뚜따" 마무리(Bip001 이동·Armature/Animator 정리), Prefab 호환성(작업용 복사본 생성)까지 추가됨.

### 파일 구조

```
Packages/com.yamo.unitytools/Editor/BipedConverter/
├── BipedConverter.cs          # 코어 변환 로직 (namespace: YAMO.UnityTools.Editor)
├── BipedConverterWindow.cs    # EditorWindow UI (Hub 임베드용 DrawGUI() 포함)
├── BipedTemplate.fbx          # 보조 샘플 FBX
├── Templates/                 # 캐릭터별 Biped 템플릿 FBX 보관 폴더
│   └── *.fbx                  # 캐릭터 추가 시 이곳에 배치 (마누카/아이리/키쿄/시오/시나노 등)
└── HANDOFF.md                 # 이 문서 (스크립트 전용 해설서)
```

> 폴더 전체가 `com.yamo.unitytools` 패키지에 포함되어 릴리즈에 그대로 배포됨.
> 어셈블리는 패키지 루트의 `YAMO.UnityTools.Editor.asmdef` 가 커버.

### 사용 방법

두 가지 진입점:

1. **단독 창**: Unity 메뉴 **Tools → YAMO → Biped Converter** → 창 열기
2. **YAMO Hub**: 메뉴 **Tools → YAMO → ⚡ Tool Hub** (단축키 `6`) → 첫번째 탭 "Avatar Bake & Prefab"의 **두번째 파트** "2. Biped Converter" 폴드아웃

이후 공통 절차:

1. ObjectField에 원본 Armature 루트 GameObject 지정 (Prefab 자산도 가능)
2. 드롭다운에서 **Biped 템플릿** 선택 (Templates 폴더의 FBX들이 자동 표시됨)
3. **[검사]** — 본 매칭 점검만
4. **[생성]** — 검사 후 변환 실행. `{원본이름}_Biped` 작업본이 생성되고, 원본은 그대로 보존됨

> **템플릿 폴더는 실시간 스캔**: 윈도우에 포커스가 들어올 때 자동 갱신, 또는 ⟳ 버튼으로 수동 새로고침

---

## 핵심 동작 방식 (사용자가 직접 제안한 방식)

### 1. 작업용 복사본 → 템플릿 인스턴스화 → 위치 이동 → 사후 보정 → 정리

```
0. 원본 armatureRoot의 unpack된 작업용 복사본 생성 (`{name}_Biped`)
   - Project 프리팹 자산: 활성 씬에 InstantiatePrefab
   - 씬 프리팹 인스턴스: 복제 후 UnpackPrefabInstance(Completely)
   - 일반 씬 오브젝트: 단순 복제
   → 이후 모든 작업은 이 복사본에서 수행 (원본 보존)

1. 선택된 Templates/*.fbx 를 씬에 임시 wrapper로 인스턴스화
   → 모든 본의 회전값(local rotation)이 템플릿대로 보존됨

2. Footsteps, *Nub 류 본 제거 (사용자 요청: 생성하지 않음)

3. 각 Biped 본의 world position을 매칭되는 Sio 본의 world position으로 이동
   ★ rotation은 일절 건드리지 않음 → 템플릿의 회전값이 그대로 유지됨
   ★ 부모-자식 관계도 그대로 유지

4. Biped 축 제약 강제 (사후 보정)
   - Spine1 (Chest)을 Spine 수직선상으로 강제: X/Z = Spine과 동일, Y만 Sio
     → Clavicle 초기 회전값이 Spine1에 의존하기 때문
   - 손가락 10개 직선화 (엄지 포함):
     · Proximal 회전을 (Proximal pos → Distal pos) 방향으로 정렬
     · Intermediate/Distal을 직선상에 마디 길이대로 재배치
     · Intermediate/Distal의 local rotation은 템플릿 값 그대로 유지

5. 원본 Sio 본을 매칭되는 Biped 본 아래로 SetParent (worldPositionStays=true)

6. 정리 단계 ("뚜따" 마무리):
   - Bip001을 armatureRoot 하위로 이동 (별도 _Biped 게임오브젝트 생성하지 않음)
   - 임시 wrapper 게임오브젝트 삭제
   - 빈 Armature 컨테이너(Hips의 원래 부모) 자동 삭제
     · 단, 비표준 본(턱, 머리카락 물리 등)이 남아있으면 안전을 위해 보존
   - armatureRoot의 Animator 컴포넌트 제거 (원본 Humanoid Avatar 더 이상 유효하지 않음)
```

### 변환 후 결과 구조

```
armatureRoot ({원본이름}_Biped, Animator 제거됨)
├── Bip001
│   └── Bip001 Pelvis
│       ├── Hips (Sio, 자식 본 없음 — 모두 Biped 하위로 이동됨)
│       ├── Bip001 Spine
│       │   └── Spine (Sio)
│       │       └── Bip001 Spine1
│       │           └── Chest (Sio)
│       │               └── UpperChest? (있으면 Chest의 일반 자식으로 보존)
│       └── Bip001 L Thigh / R Thigh ...
├── Body (SkinnedMeshRenderer, 본 reference 그대로 유효)
└── Hair, Outfit ...
```

### 왜 이 방식인가

이전에 시도한 "본 방향에서 회전 계산" 방식은 다음 문제가 있었음:
- Pelvis world rotation을 fix하면 Spine 위치가 부모 회전에 끌려가서 어긋남
- bone direction 기반 회전 계산은 Biped의 axis convention과 맞지 않음
- Euler 분해 시 gimbal lock 때문에 Inspector 표시값이 의도와 다르게 나옴

→ **템플릿을 직접 사용하면 quaternion이 완벽하게 보존되어 모든 문제가 해결됨**

---

## 입력 모드 분류 (적응형 전략)

| 모드 | 입력 | 전략 |
|---|---|---|
| **A. 알려진 템플릿** | 부스 판매 베이스 아바타 (마누카/아이리/키쿄/시오/시나노 등) | Templates 폴더에 사전 제작 FBX 비치 → 사용자 선택. 가장 보편적. |
| **B. 스케일 변형판** | A의 비율 조정본 | 사용자에게 비율 복원 권장. 자동 적용 시 위치만 적응 (회전 그대로). 거의 변경 불필요. |
| **C. 오리지널 아바타** | 알 수 없는 비율/자세 | **현재 미지원**. 케이스가 적고 수동 작업이 더 적합. 필요 시 사용자가 직접 Biped FBX를 만들어 Templates에 배치. |

## 본별 변형 허용 정책 (포지션 기준)

핵심 원칙: **회전이 아니라 포지션으로 적응**. Biped는 무조건 T-pose 강체.

| 카테고리 | 본 | 처리 |
|---|---|---|
| **불변 (Anchor)** | UpperArm, Thigh, Finger Proximal | 위치는 Sio, 회전은 템플릿 그대로 |
| **상당 변형 OK** | Shoulder(Clavicle), Forearm, Calf, Spine | 위치는 Sio, 회전은 템플릿 |
| **미량 변형만** | Hand, Foot, Neck, Head, Toe, Finger Intermediate/Distal | 위치는 Sio, 회전은 템플릿 |
| **수직 강제** | Spine1 (Chest) | X/Z = Spine과 동일, Y만 Sio |
| **회전 보정** | Finger Proximal (Index/Middle/Ring/Little) | source의 Proximal→Distal 방향으로 회전 정렬, 자식은 직선상 재배치 |
| **회전 보정** | Thumb Proximal (Finger0) | 위와 동일. 엄지는 캐릭터마다 다른 base 방향이라 회전 보정 필수 |
| **무시 (자식 보존)** | UpperChest | Biped 슬롯 매핑 안 함. Chest의 일반 자식 본으로 그대로 따라옴 (Neck/Shoulder는 별도 reparent되어 빠져나감) |

---

## 본 매핑 규칙

### 위치 매핑 (Sio Humanoid → Biped 위치)

| Sio | Biped | 비고 |
|---|---|---|
| (없음) | `Bip001` | COM 위치 = `(Hips.x, avg(L/R UpperLeg.y), Hips.z)` |
| (없음) | `Bip001 Pelvis` | COM과 동일 위치 |
| Hips | (Pelvis 자식으로 reparent) | 1:1 매칭 없음 |
| Spine | `Bip001 Spine` | |
| Chest | `Bip001 Spine1` | 수직 정렬 강제 (X/Z = Spine) |
| Neck | `Bip001 Neck` | |
| Head | `Bip001 Head` | |
| LeftShoulder | `Bip001 L Clavicle` | |
| LeftUpperArm | `Bip001 L UpperArm` | |
| LeftLowerArm | `Bip001 L Forearm` | |
| LeftHand | `Bip001 L Hand` | |
| LeftUpperLeg | `Bip001 L Thigh` | **ForceComY**: y를 COM.y로 강제 |
| LeftLowerLeg | `Bip001 L Calf` | |
| LeftFoot | `Bip001 L Foot` | |
| LeftToes | `Bip001 L Toe0` | |
| (Right 모두 mirror) | | |

### 손가락 매핑 (Biped 손가락 인덱스 컨벤션)

| Sio | Biped |
|---|---|
| Thumb{Proximal/Intermediate/Distal} | Finger0 / Finger01 / Finger02 |
| Index | Finger1 / Finger11 / Finger12 |
| Middle | Finger2 / Finger21 / Finger22 |
| Ring | Finger3 / Finger31 / Finger32 |
| Little | Finger4 / Finger41 / Finger42 |

### 제거되는 본 (생성 안 함)

`Bip001 Footsteps`, `Bip001 HeadNub`, `Bip001 L/R Toe0Nub`, `Bip001 L/R Finger{0~4}Nub`

---

## 주요 결정 사항

1. **본 이름은 3ds Max Biped 정확 이름 사용** (`Bip001 L Thigh` 등). 후속 스크립트가 데이터를 이어받기 때문에 한 글자도 다르면 안 됨.

2. **COM 위치 = Thigh 높이**. Pelvis도 같은 위치. Spine은 Sio Hips 원래 위치에 배치 (= Sio Hips 높이와 Thigh 높이 차이는 Spine 첫 segment에 흡수됨).

3. **회전값은 Biped 템플릿 FBX에서 그대로 가져옴** — Unity 좌표계에서 정확한 quaternion 사용을 위해 3ds Max에서 export → Unity import → Unity에서 재export한 `Biped_Sample_Unity.fbx`를 템플릿으로 사용.

4. **Mesh / SkinnedMeshRenderer 무영향**: 원본 본의 world transform을 보존한 채 reparent하므로 본 reference가 그대로 유효.

5. **항상 작업용 복사본에서 작업**: 원본 보존 + Prefab 호환성을 위해 Convert는 항상 unpack된 복사본을 생성하고 그 위에서 동작.

---

## 검증된 Inspector 회전값 (참고)

템플릿 기반이므로 Sample FBX와 동일하게 출력되어야 함:

| 본 | Local Euler |
|---|---|
| Bip001 | (-90, 0, -90) |
| Bip001 Pelvis | (-90, 0, -90)* |
| Bip001 Spine, Spine1, Neck, Head | ~(0, 0, 0) |
| Bip001 L Clavicle | (~0, 90, -180) |
| Bip001 L UpperArm, Forearm | ~(0, 0, 0) |
| Bip001 L Hand | (-89, -91, 91) |
| Bip001 L Thigh | (~0, 180, ~3) |
| Bip001 L Calf | (~0, 0, ~-10) |
| Bip001 L Foot | (~0, ~0.6, ~6) |
| Bip001 L Toe0 | (0, 0, 90) |

\* 사용자가 이전 세션에서 Pelvis는 `(-90, 0, 90)`이라고 언급한 적 있으나, 현재는 템플릿 그대로 사용. 이슈가 다시 제기되면 그때 조정.

---

## 다음 세션에서 검토할 만한 사항

1. **멀티 템플릿 시스템 동작 검증** — Templates 폴더에 다양한 캐릭터 FBX 추가 후 드롭다운 동작
2. **Spine1 수직 정렬 시각 확인** — 캐릭터가 척추가 살짝 기울어져 있을 때 Spine1이 수직으로 잘 잡히는지
3. **손가락 직선화 시각 확인** — 약간 말려있는 손가락 입력에 대해 Biped 손가락이 일직선으로 펴지는지
4. **엄지 회전 보정 검증** — 엄지가 캐릭터마다 base 방향이 달라도 정상 정렬되는지
5. **Prefab 입력 검증** — Project 프리팹 자산, 씬 프리팹 인스턴스, 일반 씬 오브젝트 세 경우 모두 정상 동작
6. **"뚜따" 결과 구조 검증** — Bip001이 armatureRoot 하위에 들어가고, Armature 빈 컨테이너 자동 삭제, Animator 제거가 정상인지
7. **Skin/Mesh 변형 검증** — 실제 Mesh가 있는 캐릭터로 변환했을 때 deform이 정상인지
8. **Validate 출력 개선** — 본 매칭 테이블 형태로 시각화 고려
9. **(추후) Mode 자동 판별** — 본 비율 분석으로 가장 가까운 템플릿 자동 추천

---

## 작업 스타일 참고

- 사용자는 한국어로 소통
- 단계별 (한 번에 한 이슈씩) 검증을 선호
- "어느정도 근사치"는 OK이지만, 후속 스크립트가 의존하는 정확한 값(이름, 회전 quaternion)은 한 글자/소수점도 정확해야 함
- 과도한 분석보다 **즉각 수정 후 검증** 방식 선호
- 시각화 도구로 Biped wedge를 그려서 결과를 확인함
