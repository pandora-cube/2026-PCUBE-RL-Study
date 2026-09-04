# Action Masking 이해하기

이산(Discrete) 행동 중 **지금 할 수 없는 행동을 매 스텝 막아주는** 기법.
테트리스처럼 "행동은 40개인데 대부분 상황에서 몇 개는 애초에 불가능"한 환경에서 특히 크게 먹힌다.

---

## 1. 왜 필요한가

마스킹이 없으면 에이전트는 **불가능한 행동도 일단 해보고 배워야 한다.**

- 안 되는 행동을 고르면 → 아무 일도 안 일어나거나 페널티 → "이건 아니구나"를 **경험으로** 학습
- 그 경험을 모으는 데 스텝이 쓰인다. 탐색 공간이 그만큼 낭비된다.
- 게다가 "이 자리는 이미 찼다" 같은 규칙은 **환경이 이미 알고 있는 사실**이다. 신경망이 다시 배울 이유가 없다.

> 규칙으로 알 수 있는 건 규칙으로 알려주고, 신경망은 **전략**만 배우게 한다.

마스킹을 켜면 정책 분포에서 불가능한 행동의 확률이 **0**이 된다.
샘플링 자체가 안 되니 잘못된 행동을 겪지도, 거기에 그래디언트를 낭비하지도 않는다.

---

## 2. 테트리스에서는

이 프로젝트의 행동 공간은 **열(10) × 회전(4) = 40개 이산 액션**이다 (`TetrisEnv.ActionCount`).

```
action  →  rotation = action / 10,  column = action % 10
```

조각 모양·회전 상태에 따라 **오른쪽 끝 몇 열은 튀어나가서 배치 불가**다.
예를 들어 I 조각을 가로로 눕히면 4칸을 먹으므로 column 7,8,9는 불가능 → 그 액션들을 마스킹한다.

`TetrisEnv`가 판정 함수를 이미 제공한다 (상태를 바꾸지 않는 순수 조회):

```csharp
public bool IsValid(int action) => Core.CanPlace(action % Columns, action / Columns);
```

Agent 쪽 구현은 이걸 그대로 쓰면 된다:

```csharp
public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
{
    for (int a = 0; a < TetrisEnv.ActionCount; a++)
        if (!env.IsValid(a))
            actionMask.SetActionEnabled(0, a, false);   // (branch, action, enabled)
}
```

- 첫 번째 인자 `0`은 **브랜치 인덱스**. 브랜치가 여러 개면 각각 따로 마스킹한다.
- ML-Agents가 매 결정(Decision) 직전에 자동으로 호출한다. 직접 부르지 않는다.
- Heuristic 모드에서도 호출되므로, 사람이 조작해도 같은 규칙이 적용된다.

---

## 3. 주의할 점

**① 한 브랜치의 모든 행동을 막으면 안 된다.**
전부 마스킹되면 에이전트가 고를 게 없어 에러가 난다.
"할 수 있는 게 하나도 없는 상태"는 마스킹이 아니라 **에피소드 종료(게임 오버)** 로 처리해야 한다.

**② Continuous 행동에는 못 쓴다.**
마스킹은 Discrete 전용. 연속 행동은 값을 클램프하거나 보상으로 유도해야 한다.

**③ 판정 함수는 부작용이 없어야 한다.**
`WriteDiscreteActionMask`는 매 스텝 40번 호출된다. 여기서 게임 상태를 건드리면 환경이 망가진다.
`CanPlace`처럼 **조회 전용**이어야 하고, 비싸면 캐싱을 고려한다.

**④ 마스킹은 "규칙"만, "전략"은 넣지 말 것.**
"불가능한 수"는 막아도 되지만 "나빠 보이는 수"를 막으면 에이전트가 그 수를 **영원히 탐색하지 못한다.**
사람의 선입견이 성능 상한이 되어버린다.

**⑤ 관측에도 같은 정보가 있으면 좋다.**
마스킹은 학습을 빠르게 하지만, 에이전트가 "왜 안 되는지"를 이해하게 하진 않는다.
보드 점유 상태처럼 판단 근거가 되는 관측은 그대로 넣어둔다.

---

## 4. 다른 환경에서의 예

| 환경 | 마스킹 대상 |
|---|---|
| 오목·체스 | 이미 돌이 놓인 자리, 규칙상 불가능한 이동 |
| 카드 게임 | 손에 없는 카드, 낼 수 없는 카드 |
| RPG 전투 | 쿨타임 중인 스킬, 마나 부족한 스킬 |
| 격자 이동 | 벽 방향 이동, 맵 밖으로 나가는 이동 |

공통점: **환경 규칙만으로 100% 판정 가능하고, 상태에 따라 매 스텝 달라지는** 제약.

---

## 참고

- [ML-Agents — Masking Discrete Actions](https://github.com/Unity-Technologies/ml-agents/blob/main/docs/Learning-Environment-Design-Agents.md#masking-discrete-actions)
- 구현 위치: `TetrisAgent/Assets/Scripts/TetrisEnv.cs` (`IsValid`), `TetrisCore.cs` (`CanPlace`)
