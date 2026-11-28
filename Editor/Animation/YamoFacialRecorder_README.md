# YAMO Live Facial Recorder

기존 FacialRecorder / EasyMotionRecorder를 수정하거나 참조하지 않는 별도 도구입니다.
Unity Editor Play Mode에서 iFacialMocap / FACEMOTION3D 텍스트 UDP를 수신하고 `.anim`을 저장합니다.

1. 캐릭터 루트를 선택한 뒤 **Tools → YAMO → Animation → Create Live Facial Recorder**를 실행합니다.
2. Targets에 캐릭터별 Root와 Face Meshes를 등록합니다. Root는 클립을 재생할 Animator의 루트입니다.
   **각 루트에서 얼굴 메시 / Humanoid 머리·눈 본 찾기**로 하위 메시를 채울 수 있습니다.
3. 기존 FacialRecorder 없이 새 도구만 씬에 배치하고 Play에 진입합니다. 자동으로 수신을 시작하며 기본 UDP 포트는 49983입니다.
   기기 주소, 포트, Mirror를 설정할 수 있습니다. 연결 실패와 데이터 수신 상태가 게임뷰에 표시됩니다.
4. **Num 1~9 / Num 0**으로 1~10번 캐릭터를 선택합니다. 게임뷰 버튼과 Inspector에서도 전환 가능합니다.
   입력은 Game View 포커스가 필요합니다. NumLock을 켜세요. 슬롯이 10개를 넘으면 추가 슬롯은 버튼으로 선택합니다.
5. **R**로 녹화 시작, **X**로 종료·저장합니다. 게임뷰의 빨간 REC와 경과 시간으로 녹화 상태를 확인합니다.

- **FaceOnly**: BlendShape만 기록합니다. Animator / Humanoid Avatar 없이도 가능합니다. 몸·머리·눈 Transform 커브는 만들지 않습니다.
- **FaceAndHeadEyes**: 지정한 머리·눈 본의 로컬 회전도 함께 기록합니다. 몸 모션은 기록하지 않습니다.
  기기에서 본 회전도 수신하려면 Receive Head And Eyes를 켜세요.
- **녹화 중 캐릭터 전환**: 이전 대상의 클립을 저장하고 새 대상의 별도 클립을 즉시 시작합니다.
  잘못된 슬롯은 전환하지 않으며 저장 실패 시 전환을 중단하고 데이터와 저장 재시도 버튼을 유지합니다.
- Play 중 Root / Face Meshes를 변경하고 해당 슬롯을 다시 선택하면 수신·녹화 대상을 함께 재연결합니다.
  녹화 도중 Inspector의 필드를 바꿔도 진행 중인 테이크의 대상·모드·경로·FPS는 고정됩니다.
- 기본 저장 위치는 `Assets/Motion/YAMOFacial`입니다. 캐릭터 이름과 시간으로 새 파일을 생성하며 기존 파일을 덮어쓰지 않습니다.
  녹화 중단, 컴포넌트 비활성화, Play 종료 시에도 현재 테이크를 저장합니다.
- FPS는 최대 샘플 빈도입니다. 낮은 실행 FPS에서 가짜 중간 프레임을 생성하지 않고 실제 경과 시간으로 기록합니다.
- Legacy Input과 Input System만 사용하는 프로젝트를 모두 지원합니다. Input System 패키지를 강제로 추가하지 않습니다.
- Excluded Blend Shapes는 실제 메시의 BlendShape 이름을 사용합니다. 대소문자는 구분하지 않습니다.
- 녹화 저장은 Editor 전용입니다. 빌드에서는 UDP 수신·캐릭터 전환·상태 표시만 지원합니다.

기존 프리팹은 그대로 유지됩니다. 같은 포트의 기존 수신기를 켜 둔 상태에서 새 수신기를 연결하면 포트 충돌이 표시됩니다.
