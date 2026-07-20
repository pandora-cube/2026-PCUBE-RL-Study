# POCA (MA-POCA) & 멀티 에이전트 RL

**POCA = Posthumous Credit Assignment.** 협동하는 팀을 학습시킬 때 "이 결과가 팀원 각자의
행동과 어떻게 연결되는가"를 풀기 위한 ML-Agents의 트레이너다. SoccerBots는 팀 스포츠라
이 문서에서 다루는 credit assignment 문제를 그대로 갖고 있다.

---

## 1. 왜 일반 RL로는 안 되나

일반적인 단일 에이전트 정책 경사법(PPO 등)은 "이 리턴은 내 행동 때문"이라고 가정한다.
팀에 여러 에이전트가 있으면 이 가정이 깨진다.

- **팀 보상을 누구 것으로 볼 것인가.** 골을 넣으면 팀 전체에 `+1`을 주는데, 실제로는
  한 명이 슛했고 다른 한 명은 수비를 끌어줬을 뿐이다. 각자에게 얼마씩 귀속시켜야 하나?
- **에이전트 수가 늘수록 신호가 희석된다(credit assignment 문제).** 3명이 나눠 가지면
  각자 입장에서 "내 행동 하나가 리턴에 미친 영향"은 더 흐려진다.
- **먼저 죽거나 벤치된 에이전트는 어떡하나.** SoccerBots에서 골이 들어가면
  `EndGroupEpisode()`로 그 라운드가 즉시 끝난다. 초반에 좋은 수비 포지셔닝을 잡았던
  에이전트가 있어도, 골이 늦게 터지면 그 에이전트의 개별 관측·행동 시퀀스는 이미
  짧게 끊겼을 수 있다 — "사후(posthumous)"에도 그 기여를 리턴에 반영해야 한다.

이걸 각 에이전트가 서로를 독립된 환경 노이즈로 보고 개별 PPO를 돌리면
(non-stationarity 문제), 상대/팀원이 계속 바뀌는 것처럼 보여 학습이 불안정해진다.

---

## 2. 핵심 아이디어: 중앙 집중 학습, 분산 실행 (CTDE)

**Centralized Training, Decentralized Execution.**

- **학습(training) 때는** 크리틱(가치 함수)이 **팀 전체의 관측 + 행동**을 다 본다.
  "이 팀 상태에서 이 조합의 행동들이 앞으로 얼마나 좋은가"를 팀 단위로 평가할 수 있다.
- **실행(inference) 때는** 각 에이전트가 **자기 관측만 보고** 독립적으로 행동한다.
  배포된 빌드에서 스트라이커가 팀원의 관측을 실시간으로 공유받을 필요가 없다.

크리틱은 학습 중에만 쓰는 "치트키"고, 실제로 뛰는 정책(액터)은 각자 눈으로 보는 것만
쓴다. 이 비대칭 덕분에 팀 단위로 정확하게 평가하면서도, 실행 시엔 평범한 분산 에이전트로
동작한다.

POCA는 여기서 한 걸음 더 간다 — 크리틱이 "이 팀원이 있을 때"와 "없을 때"의 가치 차이를
어텐션 기반으로 비교해서, 팀 보상 중 **이 에이전트가 얼마를 기여했는지 근사적으로
분해**한다. 사람이 "누가 넣었으니 얼마"라고 정하지 않아도 된다.

---

## 3. 이 프로젝트에서의 배선

### (a) 그룹은 코드가 만든다

```csharp
// SoccerEnvController.cs
SimpleMultiAgentGroup m_BlueAgentGroup;
SimpleMultiAgentGroup m_RedAgentGroup;
```

`SimpleMultiAgentGroup`은 ML-Agents가 제공하는 팀 컨테이너다. `RegisterAgent`/
`UnregisterAgent`로 그 라운드에 누가 그 팀에 속하는지 알려준다. 커리큘럼 lesson에 따라
필드에 나오는 에이전트 수가 달라지므로(`ActiveInLesson`), `ResetScene()`마다 매번
다시 등록한다.

### (b) 보상 채널이 둘로 나뉜다

| | API | 필드 | TensorBoard |
|---|---|---|---|
| 개별 보상 | `Agent.AddReward()` | `m_Reward` | `Environment/Cumulative Reward` |
| 그룹 보상 | `SimpleMultiAgentGroup.AddGroupReward()` | `m_GroupReward` (별도) | `Environment/Group Cumulative Reward` |

`AddGroupReward(x)`는 그 그룹에 등록된 **모든 에이전트의 `m_GroupReward`에 x를 더한다.**
개별 `m_Reward`는 건드리지 않는다 — 완전히 분리된 채널이다.

**둘 다 학습(정책 업데이트)에는 들어간다.** POCA는 "개별 보상 + 그룹 보상"을 합친
리턴을 최적화하되, 그룹 보상 쪽의 credit assignment를 중앙 크리틱이 처리한다.

**하지만 커리큘럼 게이트(`completion_criteria.measure: reward`)는 개별 채널만 읽는다.**
그룹 보상은 커리큘럼 판정에 안 들어간다. 이 프로젝트에서 실제로 부딪힌 문제가 이거였다
— 골(`AddGroupReward(1)`)은 그룹 채널로만 가서, 개별 채널엔 셰이핑 페널티만 남아
`Environment/Cumulative Reward`가 음수에 갇히고 진급 threshold를 못 넘었다. 고친 방법은
골 결과를 개별 채널에도 미러링(`agent.AddReward(...)`)해서 measure가 득점을 보게 만든 것.

```csharp
// GoalTouched() 안 — 그룹 보상(학습 신호)은 그대로 두고, 개별 보상에도 결과를 반영
foreach (var item in AgentsList)
{
    var a = item.Agent;
    if (!a.gameObject.activeSelf || a.position != AgentSoccer.Position.Striker) continue;
    a.AddReward(a.team == scoredTeam ? 1f : -1f);
}
```

### (c) 왜 골을 득점자 한 명이 아니라 팀 전체에 주나

- 골은 대개 마지막 터치 한 명만의 성과가 아니다 — 드리블, 수비 끌기, 공간 창출이
  다 기여한다. 득점자에게만 주면 셋업 플레이가 무보상이 되고 패스 회피
  (ball-hogging)가 학습될 수 있다.
- 애초에 POCA 자체가 "팀 보상을 누가 얼마나 기여했는지 트레이너가 역산"하도록
  설계된 알고리즘이다. 득점자를 사람이 미리 정해버리면 이 매커니즘을 쓸 이유가 없다.
- 이 코드베이스는 애초에 어느 에이전트가 마지막으로 공을 찼는지도 추적하지 않는다
  (`SoccerBallController`는 "어느 골대에 맞았나"만 안다) — 득점자만 주려면 추적
  로직을 새로 붙여야 하는데, 위 이유로 그럴 필요가 없다.

### (d) 에피소드 종료도 그룹 단위

```csharp
scoredGroup.EndGroupEpisode();
concededGroup.EndGroupEpisode();
```

한 팀의 누구 하나가 아니라 **그룹 전체**가 동시에 에피소드를 마감한다. 도중에
`MaxEnvironmentSteps`로 타임아웃되면 `EndGroupEpisode()` 대신
`GroupEpisodeInterrupted()`를 쓰는데, 이건 "실패로 끝난 게 아니라 시간이 다 돼
끊겼다"는 걸 트레이너에 알려 마지막 상태를 실패로 오인하지 않게 한다.

---

## 4. 자기 대국(Self-Play)과의 관계

`soccer.yaml`의 `self_play` 블록은 POCA와 별개 축이다 — POCA가 **팀 내부**
credit assignment를 푼다면, self-play는 **상대 팀**을 어떻게 채우느냐를 다룬다.

```yaml
self_play:
  save_steps: 50000              # 이 주기로 현재 정책 스냅샷을 저장
  team_change: 200000            # 이 주기로 서로 팀을 바꿔가며 학습(한쪽만 강해지는 것 방지)
  swap_steps: 2000               # 상대 스냅샷을 이 주기로 교체
  window: 10                     # 최근 스냅샷 몇 개를 상대 풀로 유지
  play_against_latest_model_ratio: 0.5  # 최신 정책 vs 과거 스냅샷 대전 비율
  initial_elo: 1200.0
```

lesson 0(EmptyGoal)·1(VsGoalie)은 상대 팀 자리에 사람이 만든 고정 상대(빈 골대,
스크립트 없는 골키맆 자리)가 있어 self-play가 사실상 solo 학습이고, lesson 2(SelfPlay,
3v3)에서부터 진짜 self-play가 돈다 — 이때는 POCA(팀 내부 협동)와 self-play(팀 간 경쟁)가
동시에 작동한다.

---

## 5. 요약 표

| 질문 | 답 |
|---|---|
| 그룹 보상은 누구에게 가나 | 그 순간 그룹에 등록된 모든 에이전트에게 동일하게 |
| 개별 보상과 그룹 보상 둘 다 학습에 쓰이나 | 그렇다. 그룹 쪽 credit assignment만 크리틱이 대신 푼다 |
| 커리큘럼 게이트는 어느 채널을 보나 | 개별 채널(`Environment/Cumulative Reward`)만. 그룹 보상은 안 봄 |
| 왜 득점자 한 명이 아니라 팀에 보상하나 | 셋업 기여를 살리고, credit assignment는 POCA가 하도록 |
| 학습 때와 실행 때 다른 점 | 학습: 크리틱이 팀 전체를 봄. 실행: 각자 자기 관측만 보고 행동 |
| self-play와 POCA는 같은 것인가 | 아니다. POCA=팀 내부 분배, self-play=상대 팀 구성 전략. 둘은 별도 축 |
