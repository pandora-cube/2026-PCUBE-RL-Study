# 실습: ML-Agents 셋업

# ML-Agents 설치 & 학습 환경 구축 (Windows / macOS)

> 이 문서는 ML-Agents로 학습을 돌리기 위한 **개발 환경 구축**만 다룬다. 씬 설계·Agent 스크립트·학습 실행은 `week3/ml-agents-training-plan.md` 참고.
>
> **기준 버전** (이 스터디 = Unity 6 + `com.unity.ml-agents 4.0.x`, 즉 ML-Agents **Release 23** 트랙):
> - Unity `6000.0` 이상 (스터디 프로젝트는 `6000.3.12f1`)
> - Unity 패키지 `com.unity.ml-agents 4.0.x`
> - Python `3.10.12`
> - `torch 2.2.1`, `onnx 1.15.0`, `protobuf 3.20.0`, `numpy 1.23.5`
>
> 공식 문서: <https://docs.unity3d.com/Packages/com.unity.ml-agents@4.0/manual/Installation.html>

---

## 1. 큰 그림 — 왜 설치가 두 덩어리인가

ML-Agents 학습은 **두 프로세스가 소켓으로 대화**한다.

```
Unity (환경)  ⟷  Python (두뇌, PPO 트레이너)
  C# 패키지          mlagents 파이썬 패키지
```

- **Unity 쪽**: `com.unity.ml-agents` **C# 패키지**를 프로젝트에 설치 (Package Manager).
- **Python 쪽**: `mlagents` **파이썬 패키지**를 설치 (아나콘다 가상환경 + pip).

그래서 설치도 두 갈래다. 그리고 **둘의 버전이 서로 맞아야** 한다(communicator API 버전 호환). 아래 표의 조합을 벗어나면 "학습이 시작조차 안 되는" 대표 원인이 된다.

---

## 2. 버전 호환표

ML-Agents는 릴리스 번호(Release N)마다 Unity 패키지·Python 패키지 버전이 한 세트로 묶인다. **아무거나 최신으로 섞으면 안 되고, 한 릴리스로 통일**해야 한다.

| 항목 | 값 | 비고 |
| --- | --- | --- |
| **ML-Agents 릴리스** | Release 23 | 현재 최신 안정 릴리스 |
| **Unity** | `6000.0` 이상 (Unity 6) | 스터디: `6000.3.12f1` |
| **Unity 패키지** `com.unity.ml-agents` | `4.0.x` | Release 23 = `4.0.0` |
| **Python** | `3.10.12` | 허용 범위 `>=3.10.1, <=3.10.12`. **3.11 이상은 설치 실패** |
| **mlagents (Python)** | 소스 설치 시 `1.2.0.dev0` | PyPI 안정판은 `1.1.0`(= Release 22) |
| **PyTorch** | `~=2.2.1` | 최소 `2.1.1`, 권장 `2.2.1` |
| **onnx** | `1.15.0` (고정) | 학습 결과를 `.onnx`로 내보낼 때 필요 |
| **protobuf** | `>=3.6, <3.21` → **`3.20.0`** 권장 | 3.21↑는 충돌 |
| **numpy** | `>=1.23.5, <1.24.0` → **`1.23.5`** | 1.24↑는 충돌 |
| **grpcio** | `<=1.53.2` | macOS는 별도 이슈 있음(9장) |
| **CUDA** (GPU 사용 시) | `12.1` (`cu121` 빌드) | 8장 |

> **참고 — 구버전 트랙**: Unity 2023.2 / Unity 패키지 `3.0.0`을 쓴다면 Release 22 트랙이고, 이때 Python은 `mlagents==1.1.0`(PyPI)로 맞춘다. 이 스터디는 Unity 6이므로 위의 Release 23으로 진행한다.

---

## 3. 아나콘다(Miniconda) 설치

### 아나콘다가 뭔가

파이썬은 프로젝트마다 필요한 라이브러리 버전이 다르다. 한 컴퓨터에 전부 깔면 버전이 충돌한다. **가상환경(virtual environment)** 은 프로젝트별로 파이썬과 라이브러리를 격리된 방에 따로 담는 도구다. **아나콘다/미니콘다(conda)** 는 그 방을 만들고 관리해 주는 대표 도구다.

- **Anaconda**: conda + 데이터과학 패키지 수백 개가 딸려옴 (무겁다, 3GB+).
- **Miniconda**: conda + 파이썬 최소 구성만 (가볍다, 권장). ML-Agents엔 이걸로 충분하다.

### 설치

1. **Miniconda 다운로드**: <https://www.anaconda.com/download/success> → *Miniconda Installers* 에서 OS에 맞는 것.
   - Windows: `Miniconda3 Windows 64-bit` (`.exe`)
   - macOS (Apple Silicon M1~): `Miniconda3 macOS Apple M1 64-bit pkg`
   - macOS (Intel): `Miniconda3 macOS Intel x86 64-bit pkg`
2. **설치 실행**
   - Windows: 다운로드한 `.exe` 더블클릭 → 계속 *Next* → 완료. (옵션 "Add to PATH"는 체크 안 해도 됨. 뒤에 나오는 **Anaconda Prompt**를 쓸 것이다.)
   - macOS: `.pkg` 더블클릭 → 안내대로 진행.
3. **설치 확인** — 새 터미널을 열고(중요: 설치 후 터미널을 **새로 열어야** 인식됨):
   - Windows: 시작 메뉴에서 **"Anaconda Prompt"** 를 검색해 실행.
   - macOS: 기본 **터미널(Terminal)** 실행.

   아래를 입력해 버전이 나오면 성공:
   ```shell
   conda --version
   ```
   > macOS에서 `conda: command not found` 가 나오면 `source ~/miniconda3/bin/activate` 를 한 번 실행한 뒤 `conda init zsh` → 터미널 재시작.

### conda 기본 명령어 (이것만 알면 됨)

| 명령 | 뜻 |
| --- | --- |
| `conda create -n 이름 python=3.10.12` | `이름` 이라는 새 가상환경을 파이썬 3.10.12로 생성 |
| `conda activate 이름` | 그 환경 안으로 들어가기 (프롬프트 앞에 `(이름)` 표시됨) |
| `conda deactivate` | 환경 밖으로 나오기 |
| `conda env list` | 만들어 둔 환경 목록 보기 |
| `conda env remove -n 이름` | 환경 통째로 삭제 (꼬였을 때 지우고 다시 만들면 됨) |

---

## 4. 가상환경 만들기

Anaconda Prompt(Win) 또는 터미널(mac)에서:

```shell
conda create -n mlagents python=3.10.12
conda activate mlagents
```

- `-n mlagents` → 환경 이름을 `mlagents`로. (이름은 자유)
- 성공하면 프롬프트가 `(mlagents) ...` 로 바뀐다. **앞으로 pip 설치·학습 실행은 전부 이 `(mlagents)` 상태에서 한다.**
- 새 터미널을 열면 이 환경은 꺼져 있다. 그때마다 `conda activate mlagents` 를 다시 쳐야 한다.

파이썬 버전 확인:
```shell
python --version   # Python 3.10.12 가 나오면 OK
```

---

## 5. Unity 패키지 설치 — Package Manager에 git URL로 추가

Unity 에디터(6000.x)에서 학습시킬 프로젝트를 연 뒤:

1. 상단 메뉴 **Window → Package Manager**.
2. 좌측 상단 **`+` 버튼** 클릭 → **Add package from git URL...**
3. 아래 URL을 붙여넣고 **Add**:
   ```
   https://github.com/Unity-Technologies/ml-agents.git?path=/com.unity.ml-agents#release_23
   ```
   - `?path=/com.unity.ml-agents` → 레포 안의 해당 하위 폴더만 패키지로 가져온다.
   - `#release_23` → Release 23 브랜치(= 패키지 `4.0.0`)로 고정.
4. 잠시 뒤 패키지 목록에 **ML Agents 4.0.x** 가 뜨면 설치 완료.

> **대안(더 간단)**: Unity 6에서는 `com.unity.ml-agents 4.0.x`가 Unity 레지스트리에도 올라와 있다. Package Manager의 **Unity Registry** 탭에서 "ML Agents" 를 검색해 **Install** 해도 된다. 결과는 같다. (이 스터디 프로젝트도 레지스트리 버전 `4.0.3`을 쓴다.) 다만 과제 요구대로 git URL 방식을 기본으로 안내한다.

설치되면 `Behavior Parameters`, `Decision Requester`, 각종 `Sensor` 컴포넌트를 Add Component에서 쓸 수 있다.

---

## 6. PyTorch 설치 (CPU 기본)

**GPU가 없거나 macOS라면** 이 절만 하면 된다. **NVIDIA GPU로 CUDA 가속을 쓸 것**이면 이 절을 건너뛰고 **8장**으로.

`(mlagents)` 환경에서:

```shell
pip install torch~=2.2.1
```

- macOS(Apple Silicon 포함)는 CUDA가 없다. CPU 빌드로 설치되며, ML-Agents 예제 학습에는 이걸로 충분하다.
- `~=2.2.1` 은 "2.2.x 중 최신"을 뜻한다.

---

## 7. mlagents 파이썬 패키지 설치

Unity 패키지 `4.0.x`와 **버전을 맞추려면 소스 설치**가 정석이다(PyPI 안정판 `1.1.0`은 Release 22용이라 Unity 4.0.x와 섞으면 communicator 경고가 날 수 있다).

### 방법 A — 소스 설치 (권장, Unity 4.0.x와 정합)

```shell
# 학습 스크립트/설정을 둘 적당한 위치에서 (예: 홈 디렉토리)
git clone --branch release_23 https://github.com/Unity-Technologies/ml-agents.git
cd ml-agents

# 반드시 envs 를 먼저, 그 다음 본체
python -m pip install ./ml-agents-envs
python -m pip install ./ml-agents
```

- 이렇게 하면 `mlagents 1.2.0.dev0` 이 설치되고, 예제 환경·설정 파일(`config/*.yaml`)도 함께 받는다.
- `torch`, `onnx==1.15.0`, `protobuf(<3.21)`, `numpy(1.23.5)` 등 의존성은 자동으로 딸려 온다. (6장에서 이미 torch를 깔았으면 그 버전을 유지한다.)

### 방법 B — PyPI 간편 설치 (빠르지만 버전 주의)

```shell
pip install mlagents==1.1.0
```

- 한 줄로 끝나지만 이는 Release 22(Unity 패키지 `3.0.0`)에 맞춘 버전이다. Unity 패키지가 `4.0.x`면 학습 시작 시 communicator 버전 mismatch **경고**가 뜰 수 있다(대개 동작은 하지만 정식 조합은 아님). **Unity 6/4.0.x를 쓰는 이 스터디는 방법 A를 권장.**

---

## 8. GPU 있는 경우 — CUDA 연동

NVIDIA GPU가 있으면 학습을 크게 가속할 수 있다. (macOS·AMD GPU는 해당 없음 → 6장 CPU 설치로.)

### 8-1. GPU / 드라이버 확인

터미널(또는 Anaconda Prompt)에서:
```shell
nvidia-smi
```
- 표가 뜨고 우측 상단에 **CUDA Version: 12.x** 가 보이면 OK. 이 값은 드라이버가 지원하는 **최대** CUDA 버전이며, 그보다 낮은 12.1 런타임 빌드는 문제없이 돌아간다.
- 명령이 안 먹히면 NVIDIA 그래픽 드라이버부터 설치/업데이트: <https://www.nvidia.com/Download/index.aspx>

> 별도로 CUDA Toolkit을 시스템에 설치할 필요는 **없다**. 아래 PyTorch 휠(`cu121`)이 필요한 CUDA 런타임을 자체 포함한다. 최신 그래픽 드라이버만 있으면 된다.

### 8-2. CUDA 지원 PyTorch 설치

**6장을 건너뛰고** 여기서 설치한다. 만약 6장에서 CPU 버전을 이미 깔았다면 먼저 지운다:
```shell
pip uninstall -y torch
```

그 다음 CUDA 12.1 빌드 설치 (`(mlagents)` 환경에서):
```shell
pip install torch~=2.2.1 --index-url https://download.pytorch.org/whl/cu121
```

> **Windows 주의**: Visual C++ 재배포 패키지가 없으면 torch import 시 DLL 에러가 난다. 필요 시 설치: <https://support.microsoft.com/help/2977003>

### 8-3. CUDA 인식 검증

```shell
python -c "import torch; print(torch.__version__, torch.cuda.is_available())"
```
- `2.2.1+cu121 True` 처럼 **`True`** 가 나오면 GPU 학습 준비 완료.
- `False` 면: 드라이버 최신화 → CPU 버전이 남아있는지(`pip list | findstr torch` / `pip list | grep torch`) 확인 후 재설치.

이후 학습은 `mlagents-learn` 이 알아서 GPU를 쓴다(신경망이 작아 항상 빨라지는 건 아니지만, 시각 관측·큰 네트워크에서 이득).

---

## 9. 설치 검증

`(mlagents)` 환경에서:

```shell
mlagents-learn --help
```
- 사용법과 옵션이 주르륵 출력되면 Python 쪽 설치 성공.

핵심 버전 한 번에 확인:
```shell
python -c "import mlagents_envs, torch, onnx; print('mlagents_envs', mlagents_envs.__version__); print('torch', torch.__version__); print('onnx', onnx.__version__)"
```

Unity 쪽은 5장에서 패키지 목록에 **ML Agents 4.0.x** 가 보이면 OK.

> 전체 연동(Unity ↔ Python) 검증과 실제 학습 실행은 `week3/ml-agents-training-plan.md` 의 학습 절차에서 이어간다.

---

## 10. 트러블슈팅 (함정 체크리스트)

| 증상 | 원인 | 해결 |
| --- | --- | --- |
| `conda: command not found` | 설치 후 터미널을 새로 안 열었거나 PATH 미설정 | 터미널 재시작. mac은 `conda init zsh` 후 재시작 |
| 파이썬 패키지 설치가 줄줄이 실패 | Python 3.11+ 사용 | **3.10.12** 로 환경 재생성 (`conda create -n mlagents python=3.10.12`) |
| `protobuf` 관련 에러 | 3.21 이상이 깔림 | `pip install protobuf==3.20.0` |
| `numpy` 버전 충돌 | 1.24 이상이 깔림 | `pip install "numpy>=1.23.5,<1.24"` |
| (macOS) 학습 시 `cygrpc ... symbol not found '_CFRelease'` | grpc 런타임 문제 | `pip install grpcio` 재설치 |
| (Windows) torch import 시 DLL 오류 | VC++ 재배포 패키지 없음 | 위 8-2 링크에서 설치 |
| `torch.cuda.is_available()` = False | CPU 빌드 잔존 / 드라이버 구버전 | torch 재설치(`cu121`), 그래픽 드라이버 최신화 |
| 학습 시작 시 communicator 버전 **mismatch 경고** | Unity 패키지 ↔ Python 패키지 릴리스 불일치 | 둘 다 Release 23으로 통일(방법 A 소스 설치) |
| 환경이 완전히 꼬임 | 의존성 엉킴 | `conda env remove -n mlagents` 후 3~7장 재실행 |

---

## 참고 자료

- Unity, "Install the ML-Agents Toolkit" (`com.unity.ml-agents@4.0`) — <https://docs.unity3d.com/Packages/com.unity.ml-agents@4.0/manual/Installation.html>
- Unity-Technologies/ml-agents, `docs/Installation.md` (release_23)
- Unity Manual, "Install a UPM package from a Git URL" — <https://docs.unity3d.com/Manual/upm-ui-giturl.html>
- Miniconda 설치 — <https://www.anaconda.com/download/success>
- PyTorch 설치 옵션 — <https://pytorch.org/get-started/locally/>
