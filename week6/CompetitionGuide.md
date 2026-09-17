# RacingBot Cup — 참가 가이드

> 기획서: [기획-레이싱_대회.md](기획-레이싱_대회.md)
> 이 문서는 **실제로 손을 움직이는 순서**만 담습니다.

---

## 0. 한눈에

```
씬 열기 → 트랙 Randomize → 센서·관측 설계 → 학습 → 평가 실행 → [결과 제출] 버튼
```

**채점은 전부 여러분의 PC에서 돌아갑니다.** 서버는 순위만 매깁니다.

---

## 1. 준비 (한 번만)

Unity 6.3 LTS로 `week6/RacingBotCompetition` 을 엽니다. 필요한 패키지(ML-Agents 4.1.0, Splines)는 이미 들어 있습니다.

메뉴에서 한 번 실행합니다:

```
RacingBotCup → Build Scenes
```

`Assets/RacingBotCup/` 아래에 프리팹 2개와 씬 2개가 생깁니다. 씬을 망가뜨렸다면 언제든 다시 눌러 복구할 수 있습니다.

파이썬 쪽:

```bash
pip install mlagents
```

---

## 2. 학습 씬 — 열면 바로 트랙과 차가 있습니다

`Assets/RacingBotCup/Scenes/Training.unity` 를 엽니다. **Play를 누르지 않아도** 서킷과 차가 보입니다.

씬에는 독립된 훈련 환경이 **4개**(`Environment_0` ~ `Environment_3`) 나란히 놓여 있습니다. 서로 1600 m씩 떨어져 있어 물리적으로 간섭하지 않고, 각자 다른 시드로 시작해 매 에피소드 독자적으로 트랙을 다시 뽑습니다. `mlagents-learn` 은 같은 Behavior Name(`Racer`)을 쓰는 에이전트를 모두 하나의 정책으로 묶어 학습하므로, 4개가 동시에 굴러가면 그만큼 스텝당 경험이 늘어 **체감 학습 속도가 빨라집니다** — 4개의 독립된 유니티 프로세스를 띄우는 것과 별개로, 씬 하나 안에서 얻는 병렬성입니다.

각 `Environment_N` 안의 구성은 동일합니다.

| 씬 오브젝트 | 역할 |
|---|---|
| `Environment_N/Circuit` | 트랙. 시드 하나가 서킷 하나를 결정합니다 |
| `Environment_N/RaceCar` | 봉인된 차량. 자식 `Agent` 가 여러분이 수정할 부분입니다 |
| `Environment_N/TrainingArena` | 에피소드 관리 (완주·이탈·시간초과 판정, 트랙 교체) |
| `Main Camera` (씬 최상위, 공용) | 4대 중 한 대를 자동으로 따라가는 카메라 + 계기판 |

카메라는 항상 **넷 중 하나만** 보여줍니다 — 학습 자체는 4개 모두 동시에 진행되지만, 화면에는 그중 한 환경만 나온다는 뜻입니다.

### 트랙 바꾸기

`Environment_N/Circuit` 을 선택하고 인스펙터의 **[Randomize]** 를 누르면 그 환경의 서킷만 즉시 다시 생성되고 차가 새 스타트 라인으로 옮겨집니다. **[Rebuild]** 는 현재 시드를 다시 만듭니다. 4개를 한꺼번에 되돌리고 싶다면 메뉴에서 **RacingBotCup → Build Scenes** 를 다시 누르세요.

코드에서도 부릅니다:

```csharp
GetComponent<TrackInstance>().Randomize();        // 아무 서킷
GetComponent<TrackInstance>().Randomize(12345);   // 특정 시드
```

`TrainingArena` 의 **Randomize Each Episode** 가 기본으로 켜져 있어, 학습 중에는 에피소드마다 새 서킷이 나옵니다. 한 트랙만 외운 정책은 여기서부터 점수가 오르지 않습니다.

---

## 3. 에이전트 설계 — 여기가 대회의 본질

`Assets/RacingBotCup/Prefabs/RacerAgent.prefab` 을 **복사해서** 자기 것으로 만드세요.

### 3-1. 관측 코드

`RacerAgent` 를 상속하고 `CollectObservations` 를 채웁니다.

```csharp
using RacingBotCup.Agent;
using Unity.MLAgents.Sensors;

public class MyRacer : RacerAgent
{
    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(Car.ForwardSpeed / 50f);
        sensor.AddObservation(Projection.Lateral / (Projection.Width * 0.5f));
        sensor.AddObservation(WaypointLocal(20f).x / 50f);
        sensor.AddObservation(CurvatureAhead(30f) * 30f);
    }
}
```

베이스 클래스가 열어 주는 것:

| | 내용 |
|---|---|
| `Car` | `ForwardSpeed` `LocalVelocity` `LocalAngularVelocity` `SteerAngleNormalized` `SlipAngle` `WheelsOffTrack` |
| `Projection` | `Lateral`(센터라인 부호거리) `Width` `Curvature` `IsOnRoad` |
| `WaypointLocal(m)` | m 앞 센터라인 지점을 **차 기준 좌표**로 |
| `CurvatureAhead(m, window)` | 전방 구간 평균 곡률 |
| `SectionAhead(m)` | 다음 구간의 **타입**과 브레이킹 존 여부 |
| `LapProgress` `TimeSinceCheckpoint` `IsOffTrack` | 랩 진행 상태 |

`SampleRacerAgent.cs` 에 전부 쓰는 예시가 있습니다. 그대로 베껴 시작해도 됩니다.

> **관측 수를 바꾸면 BehaviorParameters의 Space Size도 바꿔야 합니다.** 어긋나면 첫 결정에서 예외가 납니다.

### 3-2. 센서

레이·카메라·그리드 센서는 **인스펙터에서 컴포넌트로 붙입니다.** 개수·각도·거리에 제한이 없습니다.

레이 센서를 추가할 때 두 가지만 맞춰 주세요:

- **Detectable Tags**: `Track`, `OffTrack`, 그리고 장애물/램프 구간을 인식하려면 `Obstacle`, `Ramp`도 추가
- **Ray Layer Mask**: `Vehicle` 레이어 제외 (안 그러면 자기 차체를 맞힙니다)

### 3-3. 보상

`OnDriveApplied` 와 두 에피소드 훅에서 씁니다.

```csharp
protected override void OnDriveApplied(float steer, float throttle) { AddReward(...); }
public override void OnLapCompleted(float seconds) { AddReward(...); }
public override void OnRunFailed() { AddReward(...); }
```

**보상은 채점에 전혀 개입하지 않습니다.** 마음껏 바꾸세요.

---

## 4. 학습

1. `Training.unity` 를 열고, **`Environment_0`~`Environment_3` 네 곳 모두**에서 `RaceCar` 의 자식 `Agent` 를 자기 프리팹으로 교체합니다. 하나라도 원본 `RacerAgent`(샘플)를 그대로 두면 그 환경은 다른 정책을 굴리는 셈이라 경험이 섞이지 않습니다 — 넷 다 같은 프리팹이어야 하나의 정책으로 학습됩니다.
2. 터미널에서:

```bash
mlagents-learn week6/config/racer_ppo.yaml --run-id=my-racer-01
```

3. Unity에서 **Play**.

끝나면 `results/my-racer-01/Racer.onnx` 가 나옵니다. `Assets/` 아래 아무 곳에나 넣으면 임포트됩니다.

---

## 5. 평가

`Assets/RacingBotCup/Scenes/Evaluation.unity` 를 열고 `RaceEvaluator` 를 선택합니다. 채울 칸은 **네 개**입니다.

| 칸 | 넣을 것 |
|---|---|
| GitHub ID | 리더보드에 뜰 이름 |
| 한 줄 설명 | 선택 |
| **내 에이전트 → Agent Prefab** | 여러분의 프리팹 |
| **내 에이전트 → Model** | 학습된 `.onnx` |

**[평가 실행]** 을 누르면 Play 모드로 들어가 **10개 서킷이 한꺼번에** 생성되고, 각 서킷에서 **베이스라인 봇(반투명 고스트)과 여러분의 차가 나란히** 달립니다. 30초 안쪽에 끝납니다.

관전하고 싶으면 **Watch Mode** 를 켜세요. 실시간으로 낮추고 카메라가 차를 따라갑니다(20분쯤 걸립니다).

> **제출용 기록은 Watch Mode를 끄고 재세요.** 실측해 보면 Watch Mode에서 랩타임이 **0.3~0.7초(약 0.7%) 느리게** 나옵니다. 물리 스텝마다 렌더 프레임이 하나씩 끼면서 PhysX 내부 상태가 미세하게 달라지기 때문입니다. 봇과 에이전트가 똑같이 느려지므로 점수(둘의 비율)는 거의 그대로지만, 아래 봇 기록표는 Watch Mode를 끈 값입니다.

### 점수

```
S_t = clamp(T_baseline / T_agent, 0, 2.0)   # 완주
S_t = 0                                     # DNF / 타임아웃
총점 = 평균(S_t)
```

봇 타임이 곧 `S = 1.0` 입니다. 같은 PC에서 같은 조건으로 잰 두 기록의 비율이라 PC 성능 차이는 대부분 상쇄됩니다.

참고용 봇 기록:

| 시드 | 봇 타임 | | 시드 | 봇 타임 |
|---|---|---|---|---|
| 3248 | 38.7s | | 3171 | 68.0s |
| 3061 | 42.1s | | 3099 | 67.0s |
| 3225 | 57.0s | | 3009 | 69.3s |
| 3307 | 58.7s | | 3194 | 76.6s |
| 3033 | 67.4s | | 3148 | 78.1s |

### 주행 규칙

| 항목 | 규칙 |
|---|---|
| 랩 | 스탠딩 스타트 후 1랩 |
| 체크포인트 | 24개를 순서대로 통과해야 인정 |
| 이탈 | 4륜이 트랙 밖인 상태가 **3초** 지속되면 DNF |
| 타임아웃 | 봇 타임 × 3 |

트랙 밖에는 **벽이 없습니다.** 대신 그래블 런오프는 접지력이 절반 이하라 나가면 그냥 느려집니다.

노면 경계는 눈으로 바로 읽힙니다: 아스팔트 → 빨강·흰색 커브(한 바퀴 전 구간) → 모래색 런오프 → 초록 잔디.

> **커브가 곧 트랙 경계선입니다.** 커브 폭 1.4 m 중 안쪽 0.3 m만 트랙이고, 바깥 1.1 m는 이미 트랙 밖으로 계산됩니다. 즉 **커브를 절반 이상 타면 그 순간부터 접지력이 절반**이고 이탈 타이머가 돌기 시작합니다. 커브를 넉넉히 쓰는 라인을 학습시킬 거라면 이 점을 알고 있어야 합니다.

---

## 6. 제출

평가가 끝나면 인스펙터의 **트랙별 결과** 에 10줄이 뜹니다. **[결과 제출]** 을 누르면 끝입니다 — 복사도, 브라우저도 필요 없습니다.

몇 번을 내도 괜찮습니다. **가장 좋은 제출 하나만 리더보드에 남습니다.**

결과 파일은 `EvaluationResults/` 에도 남습니다. Play 모드를 나간 뒤 제출하려면 **[최근 결과 불러오기]** 를 먼저 누르세요.

---

## 7. 알아두면 좋은 것

### 화면에 뜨는 것들

| | |
|---|---|
| 위쪽 시계 | 현재 랩 타임. 완주하면 초록색으로 굳고 `LAP COMPLETE` 가 뜹니다. 학습 중에는 아래에 직전 랩(`LAST`)이 함께 표시됩니다 |
| 오른쪽 아래 | 속도(km/h)와 속도 바. 네 바퀴 중 하나라도 트랙 밖이면 빨간 `OFF TRACK` |
| 노면의 검은 자국 | 타이어가 미끄러진 흔적 |

타이어 자국은 **휠스핀·락업·드리프트일 때만** 남습니다. 코너마다 검은 줄이 그어진다면 그만큼 옆으로 미끄러지고 있다는 뜻이고, 대개 그 구간에서 시간을 잃고 있습니다. 관측 없이 정책의 버릇을 보는 가장 싼 방법입니다.

기록에는 아무 영향이 없습니다 — 전부 `LateUpdate` 에서 도는 그리기 전용 코드이고, 채점이 도는 `Physics.Simulate` 경로에는 손대지 않습니다. 빠른 평가(Watch Mode 끔)에서는 아예 꺼집니다.

### 서킷은 어떻게 생겼나

무작위 곡선이 아니라 **F1식 섹션의 조합**입니다.

| 섹션 | 성격 | 공략 |
|---|---|---|
| `Straight` | 170~375 m 전개 구간 | 최고속. 끝에서 브레이킹 |
| `Corner` | 리프트 없이 도는 스위퍼 | 라인 유지 |
| `Hairpin` | 반경 14~19 m, 랩 최저속 | 시간이 가장 크게 갈립니다 |
| `Chicane` | 짧은 좌-우 급전환 | 늦게 들어가면 반대쪽 런오프행 |
| `Esses` | 연속 코너 | 속도를 이어가는 것이 관건 |
| `SharpHairpin` | 헤어핀보다 급함, 내각 최대 ~53° | 랩 최저속. 안쪽 정점을 콘크리트 배리어(`Obstacle` 태그)가 막고 있어 숏컷 불가 |
| `ObstacleStraight` | 직선에 크레이트·통나무·컨테이너가 무작위 배치 (`Obstacle` 태그) | 최소 3m 폭의 통과 가능한 틈이 항상 있지만, 매번 다른 라인으로 피해야 합니다 |
| `RampCorner` | 코너 자체가 안쪽으로 바짝 당겨진 하나의 레이싱 라인이고, 그 위에 램프(`Ramp` 태그)가 있음 | 대안 경로가 아니라 그 코너의 유일한 라인입니다 — 오프트랙·체크포인트 걱정 없이 잘 넘으면 시간을 법니다. 착지만 잘 제어하면 됩니다 |

**세 섹션은 연습용 무작위 시드에서만 25% 확률로 등장합니다.** 평가용 고정 10개 시드
(`eval_seeds.json`)와 베이스라인 봇 기록은 이 기능과 무관하게 그대로입니다.

헤어핀·시케인 **직전 직선은 헤비 브레이킹 존**입니다 — 생성 문법이 보장하므로 모든 서킷에 최소 한 곳은 있습니다. `SectionAhead()` 로 관측에 넣을 수 있습니다.

`RacingBotCup → Track Preview` 로 시드별 서킷을 미리 볼 수 있습니다.

### 평가 시드는 공개되어 있고 고정입니다

10개 시드는 `Assets/RacingBotCup/Config/eval_seeds.json` 에 그대로 적혀 있습니다. 즉 **그 트랙들에 맞춰 학습하는 것이 가능합니다.** 채점이 여러분 PC에서 도는 이상 숨길 방법이 없기 때문입니다.

> **진짜 승부는 결선입니다.** 결선은 운영진이 **아무도 본 적 없는 트랙**에서 직접 돌립니다 (기획서 §8). 10개 트랙에만 맞춘 정책은 거기서 무너집니다.

연습용으로는 `0 ~ 100,000` 대역의 아무 시드나 쓰면 됩니다.

### 결정론

같은 제출을 두 번 채점하면 거의 같은 점수가 나옵니다. 실측 결과 10개 트랙 중 2개는 랩타임이 완전히 동일했고 나머지도 **최대 0.01초**(0.015%) 차이였습니다. 물리 타임스텝(0.02s)·의사결정 주기(4스텝)·추론 모드가 전부 고정되어 있습니다.

완벽한 비트 일치는 아닙니다 — 10개 환경이 한 물리 씬에서 동시에 도는 만큼 PhysX 부동소수점 편차가 남습니다. 점수 영향은 0.0002 수준입니다. **0.1초 이상 차이가 반복되면 버그이니 알려주세요.**

### 자주 겪는 문제

| 증상 | 원인 |
|---|---|
| 첫 결정에서 예외 | 관측 개수와 BehaviorParameters의 Space Size 불일치 |
| 레이가 전부 0 거리로 맞음 | Ray Layer Mask에서 `Vehicle` 레이어를 빼지 않음 |
| 모델을 지정했는데 키보드로 움직임 | `.onnx` 가 아직 임포트되지 않음 |
| 봇보다 한참 느림 | 정상입니다. `S = 1.0` 이 봇과 동급이고, 처음엔 0.5~0.8이 흔합니다 |
| 전부 DNF | 3초 이탈 규칙에 걸리는 중. 보상에서 센터라인 유지 비중을 올려 보세요 |
| 제출이 실패함 | 폼이 로그인 필요로 설정됨. 운영진에게 알려주세요 |
