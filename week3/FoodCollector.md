# FoodCollector — ML-Agents 학습 계획

Rogue 캐릭터가 스스로 돌아다니며 **고기(Meat)는 먹고 당근(Carrot)은 피하도록** 강화학습으로 훈련시키는 전체 절차. 코드는 나중에 작성하고, 이 문서는 "무엇을 · 어떤 순서로 · 왜" 하는지 설명하는 계획서다.

> 이 계획은 현재 프로젝트 실제 상태를 확인하고 작성했다.
> - Unity `com.unity.ml-agents 4.0.3` **이미 설치됨** (별도 패키지 설치 불필요)
> - 참조 repo: `~/Documents/Projects/Unity/ml-agents` (`release/4.0.0` 브랜치, python `mlagents 1.2.0.dev0`)
> - 씬(`MainScene`): `TrainingArea / Court / Floor`, `wall1~4`, `Rogue`(태그 `Player`), `Meat`×8, `Carrotfbx`×5
> - 움직임은 `PlayerController.cs`(tank control, New Input System)로 이미 동작 — 이 로직을 재사용한다
> - 태그는 `Player`만 존재. `food` / `badFood` / `wall` 태그는 **아직 없음**

---

## 0. 큰 그림 — 강화학습으로 무엇을 하는가

강화학습(RL)은 **에이전트가 환경에서 행동하고, 보상을 받으며, 보상을 최대화하는 정책(policy)을 스스로 학습**하는 방법이다. 이 문제를 RL 요소로 나누면:

| 요소 | 이 프로젝트에서 | 설명 |
|------|----------------|------|
| **Agent** | Rogue 캐릭터 | 관측을 보고 행동을 결정 |
| **State / Observation** | 주변 고기·당근·벽의 위치(레이 센서), 자기 속도 | "지금 무엇이 보이는가" |
| **Action** | 전진/후진, 좌/우 회전 | tank control 2개 연속값 |
| **Reward** | 고기 +1, 당근 −1, 시간 패널티 −작은값 | "잘했는가" 신호 |
| **Episode** | 리셋~일정 스텝(MaxStep)까지 한 판 | 반복하며 학습 |

학습 과정은 두 프로세스가 소켓으로 대화한다:
- **Unity(에디터/빌드)** = 환경. 관측을 보내고 행동을 받아 물리 시뮬레이션.
- **Python(`mlagents-learn`)** = 두뇌(PPO 트레이너). 관측을 받아 행동을 정하고, 보상으로 신경망을 업데이트.

학습이 끝나면 Python이 만든 신경망(`.onnx`)을 Unity에 넣어 **Python 없이도** 에이전트가 스스로 움직이게 한다(추론 모드).

---

## 1. 사전 준비 — 왜/무엇을 먼저 확인하나

학습 코드를 만들기 전에 "이 씬이 RL 환경으로서 성립하는가"를 먼저 맞춘다. 순서:

1. **파이썬 학습 환경** 구축 (2장)
2. **Agent 스크립트** 설계·작성 (3~4장)
3. **컴포넌트 부착·설정** (5장)
4. **보상 설계 확정** (6장)
5. **학습 설정(YAML)** 작성 (7장)
6. **학습 실행 → 모니터링 → 모델 적용** (10~12장)

---

## 2. 파이썬 학습 환경 구축

### 2.1 왜 별도 가상환경인가
ML-Agents Release 4는 보통 **Python 3.10.x**를 요구한다(torch/mlagents 의존성). → **3.10 전용 venv를 따로 만든다.**

### 2.2 단계
1. Python 3.10.x 설치 (pyenv / python.org / conda 중 택1).
2. 가상환경 생성·활성화 (예: `python3.10 -m venv .venv-mlagents` → `source .venv-mlagents/bin/activate`).
3. ML-Agents 설치 — **버전 정합이 중요**하다. Unity 패키지가 `4.0.3`이므로 python도 Release 4에 맞춘다. 두 방법:
   - (권장) 로컬 참조 repo에서 editable 설치 → 버전이 정확히 일치:
     `pip install -e ~/Documents/Projects/Unity/ml-agents/ml-agents-envs` 후 `pip install -e ~/Documents/Projects/Unity/ml-agents/ml-agents`
   - (간편) PyPI: `pip install mlagents` (설치되는 버전이 Unity 패키지와 호환되는지 확인)
4. 설치 검증: `mlagents-learn --help` 가 정상 출력되면 OK.

> **함정 체크리스트**: 파이썬 버전 불일치, torch 설치 실패(맥이면 CPU 빌드로 충분), Unity↔Python communicator 버전 mismatch 경고. 셋 다 "학습이 시작조차 안 되는" 대표 원인이다.

---

## 3. Agent 스크립트 설계 (구현할 내용 — 코드는 다음 단계)

핵심 전환: **`PlayerController`(MonoBehaviour) → `Agent`(Unity.MLAgents.Agent) 상속 클래스**. 움직임 로직은 그대로 재활용하고, ML-Agents 생명주기 메서드만 얹는다.

> 기존 `Assets/Scripts/FoodCollectorAgent.cs` 파일은 지금 낡은 `FoodAgent` 클래스(안 쓰는 잔재)다. 이 파일을 진짜 `Agent`로 새로 채우거나, `PlayerController`를 승격시키면 된다. **하나의 Agent 스크립트만 남긴다.**

### 3.1 재사용할 것 (이미 검증된 로직)
- 이동: `rb.AddForce(transform.forward * forward * moveSpeed, VelocityChange)` + 최고속도 클램프(`sqrMagnitude > 25 → *0.95`)
- 회전: `rb.MoveRotation(...)` (벽에 붙어도 회전되는, 지난번 버그 픽스 적용본)
- 애니메이션: 속도로 `Speed` 파라미터 세팅

### 3.2 새로 구현할 Agent 오버라이드
| 메서드 | 역할 | 이 프로젝트에서 할 일 |
|--------|------|----------------------|
| `Initialize()` | 최초 1회 셋업 | Rigidbody/Animator 캐시, 초기 위치 저장 |
| `OnEpisodeBegin()` | 매 에피소드 리셋 | 에이전트 위치·회전·속도 초기화, 음식들 재배치(`Collectible.Respawn`) |
| `CollectObservations(sensor)` | 벡터 관측 추가 | 로컬 속도 x,z 정도(대부분은 레이 센서가 담당) |
| `OnActionReceived(actions)` | 행동 적용 + 보상 | 연속값 2개로 이동/회전, 매 스텝 작은 시간 패널티 |
| `Heuristic(actionsOut)` | 사람 조작(디버그) | **`Keyboard.current`로 채운다** (레거시 Input 막힘!) |

### 3.3 행동 공간(Action Space) 결정
- **연속 행동 2개**: `[0]` 전진/후진, `[1]` 좌/우 회전
- 이산 행동 0개

행동을 줄이면 탐색 공간이 작아져 **학습이 더 빠르고 안정적**이다. (원하면 나중에 확장)

### 3.4 보상 판정 위치
- 에이전트 스크립트의 `OnTriggerEnter(other)`에서:
  - `other.CompareTag("food")` → `AddReward(+1f)` 후 `other`의 `Collectible.Respawn()`
  - `other.CompareTag("badFood")` → `AddReward(-1f)` 후 `Respawn()`
- 이렇게 하면 `Collectible.cs`의 재배치 로직을 그대로 재사용하고, 보상은 에이전트가 지급한다.

---

## 4. 관측(Observation) 설계 — "에이전트는 무엇을 보는가"

에이전트가 고기 방향으로 가고 당근을 피하려면 **주변 상황을 수치로** 받아야 한다. 두 축을 쓴다.

### 4.1 Ray Perception Sensor 3D (핵심)
`Rogue`에 **Ray Perception Sensor 3D** 컴포넌트를 붙인다. 부채꼴로 광선을 쏴서 "어느 방향에 food/badFood/wall이 얼마나 가까이 있는지"를 자동으로 관측 벡터로 변환한다. 위치를 하드코딩하지 않아도 되는, 이런 문제의 표준 해법이다.
- **Detectable Tags**: `food`, `badFood`, `wall` (3.1에서 만든 태그)
- **Rays Per Direction**: 예 5~7 (많을수록 시야 촘촘, 관측/연산 ↑)
- **Max Ray Degrees**: 예 90~120 (전방 시야각)
- **Ray Length**: 맵 크기에 맞게(고기를 충분히 멀리서 감지할 정도)
- **Sphere Cast Radius / 높이 오프셋**: 캐릭터 눈높이에 맞게 조정

### 4.2 벡터 관측 (보조)
`CollectObservations`에서 에이전트 **로컬 속도 x, z**(2개)를 추가. "지금 어디로 얼마나 빠르게 가고 있나"를 알아야 회전·정지 타이밍을 학습한다. 참조 예제도 이 두 값을 쓴다.

### 4.3 관측 크기 정리
- **Ray Sensor 관측은 자동**이라 Behavior Parameters의 `Vector Observation Space Size`에 포함되지 않는다(별도 센서로 합산됨).
- `Vector Observation Space Size`에는 `CollectObservations`가 넣는 값 개수만 적는다 → 로컬 속도 2개면 **2**.
- (선택) `Stacked Vectors`를 2~3으로 두면 직전 프레임까지 쌓아 "움직임의 추세"를 본다.

---

## 5. 컴포넌트 부착 & 설정 (에디터)

`Rogue`에 아래를 붙이고 값을 맞춘다.

1. **Agent 스크립트**(4장): `Move Speed=2`, `Turn Speed=300` 등.
2. **Behavior Parameters** (ML-Agents 제공):
   - `Behavior Name`: 예 `FoodCollector` — **학습 YAML의 키와 반드시 동일**
   - `Vector Observation` → `Space Size`: 2 (5.3), `Stacked Vectors`: 1~3
   - `Actions` → `Continuous Actions`: 2, `Discrete Branches`: 0
   - `Model`: **비워둔다**(학습 중엔 Python이 제어)
   - `Behavior Type`: `Default` (연결되면 학습, 없으면 Heuristic)
3. **Decision Requester** (ML-Agents 제공):
   - `Decision Period`: 예 5 (몇 물리스텝마다 새 결정을 내릴지. 작을수록 반응↑·연산↑)
   - `Take Actions Between Decisions`: 켜두면 결정 사이에도 직전 행동 유지(부드러움)
4. **Ray Perception Sensor 3D**(5.1).
5. **Max Step**(Agent 컴포넌트 필드): 예 5000 — 한 에피소드 최대 길이. 시간이 지나면 자동 리셋되어 다양한 상황을 학습.

---

## 6. 보상 설계 상세 — "무엇을 잘했다고 알려줄까"

보상은 RL의 전부다. 시작은 단순하게:

| 이벤트 | 보상 | 이유 |
|--------|------|------|
| 고기 먹음 | **+1.0** | 목표 행동 강화 |
| 당근 먹음 | **−1.0** | 회피 학습 |
| 매 스텝 | **−1/MaxStep** (예 −0.0002) | 빈둥대지 말고 빨리 먹도록(시간 패널티) |
| (선택) 벽 충돌 | 작은 − | 벽에 박혀 있지 않게 |

원칙:
- 보상 스케일은 대체로 **[-1, 1] 근방**으로 유지(너무 크면 학습 불안정).
- 시간 패널티는 아주 작게. 크면 "아무것도 안 하고 빨리 죽는 게 낫다"는 왜곡된 정책을 배운다.
- 처음엔 고기 +1 / 당근 −1 / 시간 패널티만으로 시작하고, 결과를 보고 조정한다.

---

## 7. 학습 설정 (YAML)

`mlagents-learn`에 넘길 설정 파일. 프로젝트 안(예: `week3/FoodCollector/config/foodcollector.yaml`)에 둔다. **최상위 키는 Behavior Name과 동일**해야 한다.

```yaml
behaviors:
  FoodCollector:                 # ← Behavior Parameters의 Behavior Name과 동일
    trainer_type: ppo            # 표준 온폴리시 알고리즘
    hyperparameters:
      batch_size: 1024
      buffer_size: 10240         # batch_size의 정수배
      learning_rate: 3.0e-4
      beta: 5.0e-3               # 탐험(엔트로피) 강도
      epsilon: 0.2               # PPO 클립 범위
      lambd: 0.95
      num_epoch: 3
    network_settings:
      normalize: false
      hidden_units: 128
      num_layers: 2
    reward_signals:
      extrinsic:
        gamma: 0.99              # 미래 보상 할인율
        strength: 1.0
    max_steps: 200000            # 총 학습 스텝(에이전트 수만큼 빨리 소모)
    time_horizon: 64
    summary_freq: 10000          # TensorBoard 기록 주기
```

각 항목 감:
- `max_steps`: 총 경험 스텝. 학습 영역을 여러 개 복제하면(9.3) 실제 시간은 크게 단축.
- `gamma`(0.99): 멀리 있는 보상을 얼마나 중시할지.
- `beta`: 초반 탐험을 늘리려면 키우고, 수렴이 안 되면 줄인다.
- `hidden_units`/`num_layers`: 문제가 어려우면 키운다(대개 128×2로 충분).

> 참조 repo의 `config/ppo/FoodCollector.yaml`을 그대로 참고해 시작값을 잡는 것도 좋다.

---

## 8. 학습 실행

### 8.1 Heuristic로 먼저 손검증
Behavior Type을 잠시 `Heuristic Only`로 두고 Play → **키보드로 조작해 고기에 닿으면 보상 로그가 찍히는지** 확인. 여기서 태그/트리거/보상 배선을 먼저 잡는다. (학습 돌리기 전에 반드시)

### 8.2 학습 시작
1. venv 활성화 상태에서:
   `mlagents-learn week3/FoodCollector/config/foodcollector.yaml --run-id=food_run_01`
2. 콘솔에 **"Start training by pressing the Play button in the Unity Editor"** 가 뜨면 → 에디터에서 **Play**.
3. Behavior Type을 `Default`로 두면 Python에 연결되어 학습이 시작된다.
4. 재시작 시 같은 run-id면 `--resume`, 처음부터면 `--force`.

### 8.3 (선택) 여러 학습 영역으로 가속
`TrainingArea`(에이전트+음식+벽 한 세트)를 통째로 복제해 4~16개 배치하면, 같은 시간에 몇 배 많은 경험이 쌓여 학습이 빨라진다. 모든 에이전트의 Behavior Name이 같으면 하나의 정책을 공유해 함께 학습한다. (영역끼리 겹치지 않게 위치만 분리)

---

## 9. 모니터링 — 학습이 되고 있나

- 새 터미널에서: `tensorboard --logdir results` → 브라우저로 그래프 확인.
- 핵심 지표: **`Environment/Cumulative Reward`** 가 우상향하면 학습 중.
  - 초반 음수(당근도 먹고 헤맴) → 점점 상승 → 양수 수렴이 이상적.
- 보조: `Policy/Entropy`(탐험, 서서히 감소), `Losses/*`.
- `mlagents-learn` 콘솔도 주기적으로 평균 보상을 출력한다.

기대: 단순한 이 문제는 수십만~수백만 스텝 내에 "고기로 직진, 당근 회피"가 눈에 보이게 나온다. 안 오르면 12장으로.

---

## 10. 학습된 모델 적용 (추론 모드)

1. 학습이 끝나면 `results/food_run_01/FoodCollector.onnx` 생성.
2. 이 `.onnx`를 `Assets/`(예: `Assets/Models/` 또는 `Assets/NNModels/`)로 복사 → Unity가 임포트.
3. `Rogue`의 **Behavior Parameters → Model** 슬롯에 그 모델을 할당.
4. **Behavior Type = `Inference Only`** 로 변경.
5. Play → **Python 없이** 에이전트가 스스로 고기를 먹고 당근을 피하면 성공.

---

## 11. 반복 · 튜닝 · 트러블슈팅

증상별 점검:
- **보상이 안 오른다** → 관측 부족(레이 길이/개수↑, 태그 누락 확인), 보상 스케일/부호, MaxStep 과소/과대, Decision Period 과대.
- **당근을 계속 먹는다** → 당근 페널티를 잠시 −1.5~−2로, 레이 센서가 badFood를 실제로 감지하는지(태그 철자!) 확인.
- **제자리에서 뱅뱅** → 시간 패널티가 과함/부족, 회전만 유리한 보상 구조인지 점검.
- **학습이 시작 안 됨** → 파이썬 버전(3.10), communicator 버전 mismatch 경고, Behavior Name ↔ YAML 키 불일치, 포트 점유.
- **닿아도 보상 없음** → 콜라이더 `isTrigger`/Rigidbody 유무, 태그 대소문자, `OnTriggerEnter` vs `OnCollisionEnter` 선택 일치.

튜닝은 **한 번에 하나씩** 바꾸고 run-id를 새로 줘서 TensorBoard로 비교한다.

---

## 부록: 실행 체크리스트

- [ ] Python 3.10 venv + ML-Agents(Release 4) 설치, `mlagents-learn --help` OK
- [ ] Agent 스크립트: Initialize/OnEpisodeBegin/CollectObservations/OnActionReceived/Heuristic(Keyboard.current)
- [ ] 보상 배선: food +1 / badFood −1 / 시간 패널티, `Collectible.Respawn` 재사용
- [ ] Ray Perception Sensor 3D (detectable tags 3종)
- [ ] Behavior Parameters(Name, Obs=2, Continuous=2), Decision Requester, Max Step
- [ ] Heuristic로 보상 로그 손검증
- [ ] YAML 작성(Behavior Name 일치)
- [ ] `mlagents-learn ... --run-id=...` → Play → 학습
- [ ] TensorBoard로 Cumulative Reward 우상향 확인
- [ ] `.onnx` → Assets → Model 할당 → Inference Only 검증
```
