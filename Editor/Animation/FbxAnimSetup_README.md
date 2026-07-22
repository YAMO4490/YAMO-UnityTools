# FBX 애니메이션 설정 도구

`Tools/YAMO/Animation/FBX 애니메이션 설정` 창은 두 개의 섹션으로 구성됩니다.
각 섹션 제목을 클릭하면 접거나 펼칠 수 있습니다.

---

## 섹션 1 · 작업 내용 및 실행 (클립 일괄 설정)

대상 FBX 목록에 올린 파일들의 **Import Settings**에 다음을 일괄 적용합니다.

- **Anim. Compression → Off**
- **클립 이름 → 파일명과 동일하게** (단일 클립 FBX 한정, 다중 클립은 기존 이름 유지)
- **Root Transform Rotation** : Bake Into Pose ✓ / Based Upon = Original
- **Root Transform Position (Y)** : Bake Into Pose ✓ / Based Upon = Original
- **Root Transform Position (XZ)** : Bake Into Pose ✓ / Based Upon = Original

### 사용법
1. Project 창에서 FBX를 선택하고 **선택 항목 추가**, 또는 **폴더 추가**로 폴더 하위 FBX를 일괄 추가합니다.
2. **설정 적용**을 누릅니다.

---

## 섹션 2 · 옵티트랙 모션 바인딩 도구

옵티트랙 등 모션캡처로 추출한 애니메이션 FBX는 기본 바인드 포즈가 정확한 T‑포즈가
아니어서 휴머노이드 리타게팅이 부정확합니다. 이 도구는 3ds Max/Blender 왕복 없이
Unity 안에서 정확한 T‑포즈 아바타를 만들어 바인딩합니다.

리스트에 올린 각 모션 FBX에 대해 다음을 자동 처리합니다.

1. **파일명을 애니메이션(클립) 이름으로 변경** — 고정 접미사 `_FBX`는 제거 (예: `드립_003`)
2. **`_T` 접미사 복사본 생성** (예: `드립_003_T`)
3. 복사본의 **Import Animation 해제** → 프레임0 회전이 구워지지 않은 **순수 T‑포즈**로 임포트
4. 복사본 **Rig = Humanoid / Create From This Model**
5. **스파인 재매핑** — 자동 매핑이 한 칸 밀려 있으므로 강제 지정
   - `Spine → {접두사}_Spine1`
   - `Chest → {접두사}_Spine3`
   - `UpperChest → {접두사}_Spine4`
   - (접두사는 액터 번호에 따라 001/002… 로 변동 → `_Hips` 본에서 자동 탐지)
6. **Left Eye / Right Eye / Jaw 매핑 제거** — Neck·Head만 유지
7. 복사본의 **Avatar를 모션 파일 Avatar로 등록** (Copy From Other Avatar)
8. 모션 파일에 **섹션 1의 클립 설정**(압축 Off · 클립명 · Root Bake)까지 적용

### 사용법
1. Project 창에서 모캡 FBX를 선택하고 **선택 항목 추가**, 또는 **폴더 추가**를 사용합니다.
2. **바인딩 실행**을 누릅니다.

### 주의사항
- 생성된 **`_T` 파일은 삭제하지 마세요.** 모션 파일이 이 파일의 Avatar를 참조합니다.
- 파이프라인 특성상 파일당 리임포트가 2회 일어나므로 대량 처리 시 시간이 걸릴 수 있습니다.
- 액터는 파일당 1명, 스파인 본 구성(Spine, Spine1~4)은 고정이라고 가정합니다.
