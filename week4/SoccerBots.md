# SoccerBots 강화학습 학습 가이드

3v3(스트라이커 2 + 골키퍼 1) 축구팀을 **MA-POCA + Self-Play**로 학습시키는 단계별 가이드.
현재 프로젝트는 `Agent` 로직이 없는 **키보드 조작 게임 상태**다. 여기서부터 RL을 붙여 학습까지 간다.

---

## 0. 현재 상태 (출발점)

| 파일 | 지금 상태 |
|---|---|
| `AgentSoccer.cs` | `MonoBehaviour`. 키보드로 이동/킥만. 관찰·액션·보상 **없음** |
| `SoccerEnvController.cs` | 공/선수 리셋 + 골 카운트만. 그룹 보상 **없음** |
| `SoccerBallController.cs` | 골 충돌 → `GoalTouched(team)` 호출 (그대로 재사용) |
| `SoccerSettings.cs` | 재질/속도 설정 (그대로 재사용) |

**우리가 채워야 할 것:** 관찰(Observation) → 액션(Action) → 그룹 보상 → 정책 분리 → 학습 설정 → 커리큘럼 → 실행/배포.

---

## 전체 그림

```
[스트라이커 정책 "Striker"]  ← 파라미터 공유 (선수 2명이 같은 뇌)
[골키퍼   정책 "Goalie"  ]  ← 별도 뇌
        │
   SimpleMultiAgentGroup (팀 3명 한 묶음, 그룹 보상 공유)
        │
   MA-POCA 트레이너 → 누가 기여했는지 크레딧 할당
        │
   Self-Play → 과거의 자기 자신과 대전하며 실력 향상
        │
   커리큘럼 → 빈 골대 → 단순 상대 → 풀 3v3 순으로 난이도 상승
```

---

## Step 1 — 파이썬 학습 환경 준비

Unity 쪽(4.0.3)과 파이썬 패키지 버전이 맞아야 한다.

```bash
# 가상환경 권장 (python 3.10.x)
python -m venv venv
source venv/bin/activate          # Windows: venv\Scripts\activate

pip install mlagents==1.1.0       # com.unity.ml-agents 4.0.3 대응
mlagents-learn --help             # 정상 출력되면 OK
```

> 버전이 안 맞으면 학습 시작할 때 protobuf/torch 에러가 난다. `mlagents-learn --help`가 깨끗이 뜨는지 먼저 확인.

---

## Step 2 — `AgentSoccer`를 다시 `Agent`로 (관찰 + 액션)

키보드 로직은 **`Heuristic`으로 이사**시키고(사람이 직접 테스트/시연할 때 씀), 진짜 조작은 `OnActionReceived`가 받게 한다.

### 2-1. 클래스 시그니처 변경

```csharp
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

public class AgentSoccer : Agent   // MonoBehaviour → Agent
{
    // team / position / speed 필드는 그대로 유지
    SoccerEnvController m_EnvController;   // 그룹 리셋 신호 받기용

    public override void Initialize()
    {
        // 지금 Awake()에 있던 초기화(속도 세팅, Rigidbody 캐싱)를 여기로
        m_EnvController = GetComponentInParent<SoccerEnvController>();
    }
}
```

### 2-2. 관찰 — 대부분은 Ray 센서가 담당

에디터에서 각 선수에 **`RayPerceptionSensorComponent3D`** 컴포넌트를 붙인다. 코드로 벡터를 일일이 쌓지 말고 센서로 처리하는 게 표준.

- **Detectable Tags**: `ball`, `blueGoal`, `redGoal`, `wall`, 아군/적 태그
- **Rays Per Direction**: 11 정도, **Max Ray Degrees**: 90~180
- **Stacked Raycasts**: `3` → 공/상대의 **움직임(속도)** 을 정책이 추론
- 골키퍼는 골문을 넓게 덮도록 각도/개수를 따로 튜닝

추가로 벡터 관찰이 필요하면(선택):

```csharp
public override void CollectObservations(VectorSensor sensor)
{
    // TODO(study): Ray로 안 잡히는 자기 상태만 최소로
    // 예: 자기 속도 sensor.AddObservation(agentRb.linearVelocity);
    //     역할/팀 원-핫 등. 너무 많이 넣지 말 것.
}
```

> BehaviorParameters의 **Stacked Vectors=3** 도 켜면 벡터 관찰도 시간축으로 쌓인다.

### 2-3. 액션 — 지금 키보드 로직을 이산 액션으로

지금 `MoveByInput()`의 3개 축(전후 / 좌우 strafe / 회전)을 **이산 브랜치 3개**로 만든다.

```csharp
public override void OnActionReceived(ActionBuffers actions)
{
    var forward = actions.DiscreteActions[0]; // 0 정지 1 전진 2 후진
    var lateral = actions.DiscreteActions[1]; // 0 정지 1 우 2 좌
    var rotate  = actions.DiscreteActions[2]; // 0 정지 1 우회전 2 좌회전

    // 기존 MoveByInput의 dirToGo/rotateDir 계산을 이 값으로 대체.
    // AddForce / transform.Rotate 물리 로직은 그대로 재사용.

    // 존재 패널티(빨리 골 넣게): AddReward(-1f / MaxStep);  // 개별 소량
}

public override void Heuristic(in ActionBuffers actionsOut)
{
    // 지금 키보드 코드를 여기로: WSQE/AD → DiscreteActions에 매핑
    var d = actionsOut.DiscreteActions;
    d[0] = Keyboard.current.wKey.isPressed ? 1 : Keyboard.current.sKey.isPressed ? 2 : 0;
    // ... 나머지 브랜치도 동일
}
```

### 2-4. 에디터 컴포넌트 세팅

각 선수 오브젝트에:
- **BehaviorParameters**: Behavior Name(아래 Step 4), Discrete Branches `[3,3,3]`, Team Id 설정
- **DecisionRequester**: Decision Period `5` 정도
- **RayPerceptionSensorComponent3D**: 위 설정
- Rigidbody / Collider: 지금 것 유지

> 스크립트 수정 후 **반드시 `read_console`로 컴파일 에러 확인** 후 다음 단계로.

---

## Step 3 — 그룹 보상 (`SimpleMultiAgentGroup`)

POCA의 핵심. 팀 3명을 한 그룹으로 묶고 **팀 성과를 공유**한다. 개별 보상이 아니라 그룹 보상이 주력이다.

`SoccerEnvController.cs`에:

```csharp
using Unity.MLAgents;

SimpleMultiAgentGroup m_BlueGroup;
SimpleMultiAgentGroup m_RedGroup;

void Start()
{
    m_BlueGroup = new SimpleMultiAgentGroup();
    m_RedGroup  = new SimpleMultiAgentGroup();
    foreach (var item in AgentsList)
    {
        if (item.Agent.team == Team.Blue) m_BlueGroup.RegisterAgent(item.Agent);
        else                              m_RedGroup.RegisterAgent(item.Agent);
    }
    // ... 기존 ballRb / 시작위치 세팅
}

public void GoalTouched(Team scoredTeam)
{
    var scored = scoredTeam == Team.Blue ? m_BlueGroup : m_RedGroup;
    var conceded = scoredTeam == Team.Blue ? m_RedGroup : m_BlueGroup;

    scored.AddGroupReward(1f - (float)m_ResetTimer / MaxEnvironmentSteps); // 빨리 넣을수록 +
    conceded.AddGroupReward(-1f);

    scored.EndGroupEpisode();
    conceded.EndGroupEpisode();
    ResetScene();
}
```

타임아웃 리셋 시엔 점수 없이 에피소드만 끊는다:

```csharp
void FixedUpdate()
{
    m_ResetTimer++;
    if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
    {
        m_BlueGroup.GroupEpisodeInterrupted();
        m_RedGroup.GroupEpisodeInterrupted();
        ResetScene();
    }
}
```

### 개별 보상 shaping (선택, 소량)

초반 학습 부트스트랩용. **스케일을 작게**(0.001~0.01) 유지해 그룹 보상을 왜곡하지 말 것.
- 스트라이커: 공을 상대 골 방향으로 전진시킬 때 +, 공 접촉 시 소량 +
- 골키퍼: 공–자기골 라인 위에 있을 때 +, 공이 자기 골 근처로 올수록 −

---

## Step 4 — 정책 분리 (Behavior Name 설계)

**가장 중요한 설계 결정.** 스트라이커 2명은 같은 뇌를 공유하고, 골키퍼는 다른 뇌를 쓴다.

| 선수 | Behavior Name | Team Id | 이유 |
|---|---|---|---|
| Blue 스트라이커 ×2 | `Striker` | 0 | 대칭 역할 → 파라미터 공유로 경험 2배, 학습 2배 빠름 |
| Red 스트라이커 ×2 | `Striker` | 1 | 같은 이름·다른 팀 → **self-play 상대**가 됨 |
| Blue 골키퍼 | `Goalie` | 0 | 하는 일이 다름(수비 위치·블로킹) |
| Red 골키퍼 | `Goalie` | 1 | 골키퍼끼리 self-play |

> 규칙: **Self-play는 "같은 Behavior Name + 다른 Team Id"** 조합으로 성립한다. 그래야 한 팀 정책을 과거 버전으로 스왑해가며 붙일 수 있다.

---

## Step 5 — 학습 설정 YAML (POCA + Self-Play)

`week4/SoccerBots/configs/soccer.yaml`:

```yaml
behaviors:
  Striker:
    trainer_type: poca
    hyperparameters:
      batch_size: 2048
      buffer_size: 20480
      learning_rate: 0.0003
      beta: 0.005
      epsilon: 0.2
      lambd: 0.95
      num_epoch: 3
      learning_rate_schedule: constant
    network_settings:
      normalize: false
      hidden_units: 512
      num_layers: 2
      vis_encode_type: simple
    reward_signals:
      extrinsic:
        gamma: 0.99
        strength: 1.0
    keep_checkpoints: 5
    max_steps: 50000000        # 축구는 2천만~5천만 스텝 필요
    time_horizon: 1000
    summary_freq: 10000
    self_play:
      save_steps: 50000
      team_change: 200000
      swap_steps: 2000
      window: 10
      play_against_latest_model_ratio: 0.5
      initial_elo: 1200.0

  Goalie:
    trainer_type: poca
    hyperparameters:
      batch_size: 2048
      buffer_size: 20480
      learning_rate: 0.0003
      beta: 0.005
      epsilon: 0.2
      lambd: 0.95
      num_epoch: 3
      learning_rate_schedule: constant
    network_settings:
      normalize: false
      hidden_units: 512
      num_layers: 2
    reward_signals:
      extrinsic:
        gamma: 0.99
        strength: 1.0
    keep_checkpoints: 5
    max_steps: 50000000
    time_horizon: 1000
    summary_freq: 10000
    self_play:
      save_steps: 50000
      team_change: 200000
      swap_steps: 2000
      window: 10
      play_against_latest_model_ratio: 0.5
      initial_elo: 1200.0
```

**self_play 파라미터 의미**
- `save_steps`: 상대로 쓸 스냅샷 저장 주기
- `team_change`: 학습 팀을 바꾸는 주기(양팀 균형 학습)
- `swap_steps`: 상대 정책 스왑 주기
- `window`: 과거 스냅샷 풀 크기(다양한 상대)
- `play_against_latest_model_ratio`: 최신 vs 과거 상대 비율

---

## Step 6 — 커리큘럼 (단계적 난이도)

처음부터 풀 3v3 self-play로 던지면 아무것도 못 배운다. C#에서 `Academy.Instance.EnvironmentParameters`로 파라미터를 읽어 난이도를 조절하고, YAML에서 그 값을 단계적으로 올린다.

C# 쪽(예: 리셋 시 공 스폰 범위·상대 활성 여부를 파라미터로):

```csharp
float ballScatter = Academy.Instance.EnvironmentParameters
                           .GetWithDefault("ball_scatter", 2.5f);
```

YAML에 `environment_parameters` 추가:

```yaml
environment_parameters:
  opponent_strength:              # 0=빈 골대/정지 → 1=풀 상대
    curriculum:
      - name: Lesson0_EmptyGoal
        completion_criteria:
          measure: reward
          behavior: Striker
          signal_smoothing: true
          min_lesson_length: 100
          threshold: 0.6
        value: 0.0
      - name: Lesson1_WeakOpponent
        completion_criteria:
          measure: reward
          behavior: Striker
          min_lesson_length: 100
          threshold: 0.6
        value: 0.5
      - name: Lesson2_FullSelfPlay
        value: 1.0
```

**권장 3단계**
1. 스트라이커: 빈 골대/정지 골키퍼 상대로 "골 넣기" 학습
2. 골키퍼: 단순 스트라이커 상대로 "막기" 학습
3. 풀 3v3 self-play로 co-adapt

---

## Step 7 — 처리량 확보 (가장 큰 실제 가속 요인)

알고리즘보다 **벽시계 시간**을 가장 크게 줄이는 건 병렬화다.

- **씬 안에 학습 필드 프리팹을 수십 개 복제** → 한 번에 수십 경기 동시 진행
- 빌드한 실행 파일로 `--num-envs` 여러 개
- `--time-scale=20` 이상

```bash
# 에디터에서 학습 (필드 여러 개 복제해 두고 ▶ 재생)
mlagents-learn configs/soccer.yaml --run-id=soccer_01 --time-scale=20

# 빌드 실행파일로 병렬 (가장 빠름)
mlagents-learn configs/soccer.yaml --run-id=soccer_01 \
  --env=Builds/Soccer.app --num-envs=8 --time-scale=20 --no-graphics

# 중단 후 이어서
mlagents-learn configs/soccer.yaml --run-id=soccer_01 --resume
```

`mlagents-learn`이 "Start training by pressing Play" 를 출력하면 에디터에서 ▶ 재생.

---

## Step 8 — 모니터링

```bash
tensorboard --logdir results
```

- **ELO**: self-play 실력 지표. 계속 우상향해야 정상(양팀이 서로 강해지는 중)
- **Environment/Cumulative Reward**: 그룹 보상 추세
- **Policy/Entropy**: 너무 빨리 0으로 떨어지면 탐험 부족 → `beta` ↑

---

## Step 9 — 학습된 모델 배포

1. 학습 끝나면 `results/soccer_01/Striker.onnx`, `Goalie.onnx` 생성
2. `Assets/`로 복사(임포트)
3. 각 선수 **BehaviorParameters**:
   - `Model` 슬롯에 해당 `.onnx` 지정
   - `Behavior Type` → **Inference Only**
4. ▶ 재생 → 학습된 AI끼리 경기

> 사람 vs AI로 하고 싶으면 사람이 조작할 선수만 `Behavior Type = Heuristic Only`로 두면 Step 2의 키보드 `Heuristic`이 동작한다.

---

## 체크리스트

- [ ] `mlagents-learn --help` 정상
- [ ] `AgentSoccer : Agent`, `OnActionReceived`/`Heuristic`/`CollectObservations` 구현, 컴파일 통과
- [ ] 각 선수에 BehaviorParameters + DecisionRequester + RayPerceptionSensor3D
- [ ] Behavior Name: 스트라이커 `Striker`, 골키퍼 `Goalie` / 팀별 Team Id 0·1
- [ ] `SimpleMultiAgentGroup`으로 팀 3명 등록, 골 시 그룹 보상 + `EndGroupEpisode`
- [ ] `configs/soccer.yaml` (poca + self_play, 두 behavior)
- [ ] 필드 복제로 병렬 환경 확보
- [ ] TensorBoard에서 ELO 우상향 확인

## 자주 나는 문제

| 증상 | 원인 / 해결 |
|---|---|
| 학습 시작 시 버전 에러 | Unity `ml-agents 4.0.3` ↔ pip `mlagents 1.1.0` 버전 불일치 |
| ELO가 안 오르고 정체 | 보상 신호 없음/약함 → 그룹 보상 연결·shaping 확인, 커리큘럼 Lesson0부터 |
| 아무 행동도 안 함 | DecisionRequester 미부착 or Behavior Type이 Inference인데 모델 없음 |
| 골키퍼가 공만 쫓아 나감 | 골키퍼 정책 분리 확인 + 자기 진영 이탈 패널티 shaping |
| 학습이 너무 느림 | 필드 복제 수 늘리기 + `--time-scale` ↑ + `--no-graphics` |

```