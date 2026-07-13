# 스터디 레포 사용법 & 규칙

강화학습 × Unity 게임 AI 스터디 공용 레포입니다.
**공용 베이스(upstream)** 와 **개인 과제(fork)** 를 분리해서 관리합니다.

---

## 기본 원칙

- 개인 과제는 **각자 fork**에 보관합니다.
- upstream(이 레포)에는 **공용으로 쓸 것만** PR로 반영합니다.
- main 브랜치는 운영진이 관리하며, 직접 push는 금지입니다.

---

## 시작하기

### 1. Fork & Clone

우측 상단 **Fork** 버튼으로 본인 계정에 복사한 뒤:

```bash
git clone https://github.com/<본인계정>/study-rl-unity.git
cd study-rl-unity
```

### 2. upstream 등록 (최초 1회)

베이스 업데이트를 받기 위해 원본 레포를 등록합니다.

```bash
git remote add upstream https://github.com/<동아리Org>/study-rl-unity.git
```

### 3. Git LFS 설치 (최초 1회)

모델·텍스처 등 대용량 파일 관리를 위해 필요합니다.

```bash
git lfs install
```

---

## 매주 작업 흐름

```bash
# 1. 최신 베이스 동기화
git fetch upstream
git merge upstream/main

# 2. 과제 브랜치 생성
git checkout -b week03-<본인이름>

# 3. 개발 후 커밋 & 푸시
git add .
git commit -m "week03: food collector 구현"
git push origin week03-<본인이름>
```

**과제 제출:** 본인 fork 링크 + 브랜치명을 스터디 채널에 공유하면 됩니다.
(개인 과제는 upstream에 PR하지 않습니다.)

---

## 디렉토리 구조

```
study-rl-unity/
├── WeekN/
│   ├── UnityProjects/
│   |   └── Assets/
|   └── configs/
├── docs/                    # 설치 가이드, 트러블슈팅
└── results/                 # 우수 결과물 링크 모음
```

---

## upstream에 반영(PR)해도 되는 것

- 공통 프리팹·씬 개선
- 버그 수정, 설치/가이드 문서 보완
- 재사용 가능한 유틸 스크립트
- (주차 종료 후) 참고용 정답 코드

## 반영하지 않는 것

- 개인 과제 결과물 → fork에만
- 개인 실험용 모델·로그
- 개인 취향의 씬 세팅

---

## 질문 & 트러블슈팅

- 채팅 대신 **Issues** / **Discussions**에 남겨주세요.
- 쌓인 기록은 다음 기수의 자산이 됩니다.
