# 변경 제안서 (Change Proposals)

이 폴더는 저장소 전체(TarkovHelper + TarkovDBEditor + 프로젝트 간 교차 작업)의
**Change Proposal**을 두는 단일 위치입니다. 이 저장소는
[change-proposal](https://github.com/josephjang/change-proposal) 프랙티스를 따르고,
프랙티스 자체는 그쪽에서 한 번만 설명합니다. 이 README는 그 위에 얹는 이 저장소만의
관례를 적습니다.

- [guide.md](https://github.com/josephjang/change-proposal/blob/main/docs/guide.md):
  Change Proposal이 무엇인지, 언제 쓰는지, 어느 형식을 고르는지, 각 섹션에 무엇이
  들어가고 무엇은 빠지는지
- [split-proposals.md](https://github.com/josephjang/change-proposal/blob/main/docs/split-proposals.md):
  Split 형식의 두 문서가 각각 맡는 것, 따로 하는 검토와 마지막 정합성 검토, 초안
  하나를 나중에 둘로 나누는 절차
- [examples/](https://github.com/josephjang/change-proposal/tree/main/examples):
  가상의 앱으로 두 형식을 채워 쓴 예시 (영어와 한국어)

이 저장소가 채택한 업스트림 리비전은 `8fba2af`이고, 채택 자체는
`2026-09-13-adopt-change-proposal.md`에 기록되어 있습니다.

## 언제 쓰는가

관찰 가능한 동작을 바꾸는 변경은 코드와 **같은 PR**에 proposal을 넣습니다. 동작이
바뀌지 않는 변경(동작이 같은 리팩터링, 오타, 의존성 패치, 테스트만)은 proposal 대신
PR 본문에 동작 변경이 없다는 것과 그것을 어떻게 확인했는지 적습니다. 기준은 변경이
**무엇을 하는가**이지, 줄 수나 걸린 시간, 사람이 썼는지 어시스턴트가 썼는지가
아닙니다. 같은 규칙이 root `CLAUDE.md`에도 있습니다.

## 두 형식

| 형식 | 파일 | 제목 | 쓰는 경우 |
|---|---|---|---|
| Unified | `YYYY-MM-DD-<slug>.md` | `Change Proposal: <이름>` | 작고 단순한 변경. 기본값 |
| Split | `YYYY-MM-DD-<slug>.requirements.md` + `YYYY-MM-DD-<slug>.design.md` | `Product Requirements: <이름>` / `Technical Design: <이름>` | 기술적 측면이 따로 설명과 검토를 필요로 할 때: 서로 얽힌 상태 전이, 데이터 마이그레이션, 컴포넌트 간 호환성, 되돌리기 어려운 아키텍처 선택 |

두 형식 모두 완전한 proposal입니다. Split의 두 문서는 함께 하나의 proposal이고, 어느
한쪽만으로는 proposal이 아닙니다. Unified로 시작해 작업 중에 기술적 복잡성이
드러나면 나눕니다: `.md`를 `.requirements.md`로 `git mv`하고 제목을 Product
Requirements로 바꾼 뒤 같은 날짜와 slug의 `.design.md`를 추가합니다 (머지 전이라
파일명이 아직 영구 주소가 아닙니다). Split의 두 문서는 제목 바로 아래에서 서로를
링크합니다.

작성은 `templates/`의 해당 템플릿을 복사해서 시작합니다: `change-proposal.md`,
`product-requirements.md`, `technical-design.md`. 섹션 이름과 순서는 고정이고
`DecisionDocsTests`가 검사합니다. 할 말이 없는 섹션은 채우지 말고 지웁니다.

Split proposal이 승인되면 `/deliver`(Codex에서는 `$deliver`)가 구현을 이어받습니다:
계획 파일, 슬라이스, 코드 가이드, PR A, 스택 브랜치의 딥 리뷰, 그 가이드, PR B.
Unified로 충분한 변경에는 그 워크플로가 필요 없습니다.

## 파일 이름과 위치

- 문서는 이 폴더에 평면으로 놓이고 **이동하거나 이름을 바꾸지 않습니다**. 파일명이
  영구 주소입니다.
- 날짜는 초안을 시작한 날입니다. 파일명이 날짜를 갖기 때문에 본문에 `Created`
  필드는 없습니다. slug는 영어 kebab-case입니다.
- **문서는 파일명으로 참조합니다** (`2026-09-13-adopt-change-proposal.md`). 폴더
  경로는 쓰지 않습니다. 코드 인용은 심볼 기준(`UserDataDbService.InitializeAsync`)
  이고 줄 번호는 참고용입니다.
- **새 문서는 영어로만** 씁니다. 기존 `.ko.md` 쌍둥이는 원본과 1:1로 유지하고, 내용
  충돌 시 영어 원본이 기준입니다. 코드 식별자(`AppLanguage.KO`, `NameKO`)는
  번역하지 않습니다. 날짜가 붙은 새 문서에는 `.ko.md` 쌍둥이를 만들지 않습니다.
- 2026-09-13 이전에 머지된 문서는 이전 형식 그대로입니다: `feature-<이름>.md` 또는
  `fix-<이름>.md`가 PRD, 같은 이름의 `.spec.md`가 spec입니다. 같은 두 역할의 옛
  이름이며, 아래 표처럼 읽습니다. 고치거나 이름을 바꾸지 않습니다.
- `archive/`는 그보다 앞선 프로세스가 남긴 동결된 문서입니다 (`YYYY-MM/`, 형식과
  위치 불변). 새 문서는 들어가지 않습니다.

| 이전 형식 | 지금 형식 |
|---|---|
| PRD (`<이름>.md`) 하나만 | Unified proposal |
| PRD + spec (`<이름>.spec.md`) | Split proposal: Product Requirements + Technical Design |
| PRD의 `Requirements / Acceptance Criteria` | `Requirements` |
| spec의 `Current Behavior / Root Cause` | Technical Design의 `Context` |
| spec의 `Verification` (실행할 명령) | `Test Strategy` (계획과 명령)와 `Verification` (실제로 실행한 결과) |
| 헤더의 `Created` 필드 | 파일명의 날짜 |

## 문서의 생애

1. **작업 전에 시작하고 작업과 함께 끝냅니다.** 구현하는 사람이나 어시스턴트는
   proposal을 보고 만듭니다. 작업 브랜치에서 쓰고 같은 PR로 머지합니다 (born
   final). `main`에 있는 문서는 곧 유효한 기록입니다. 진행 중 = 열린 PR, 완료 =
   머지된 PR, 중단 = 머지 없이 닫힌 PR. 상태는 전부 GitHub이 소유하고, 문서에는
   상태 필드가 없습니다.
2. **변경이 열려 있는 동안은 초안을 고쳐 씁니다.** 조사와 리뷰로 사실이 바뀌면
   문서를 하나의 정합적인 서술로 다시 씁니다. 뒤집힌 결정은 지우지 않고 Decisions에
   그 이유와 함께 남깁니다. PR A와 PR B로 나뉘어 배달되는 변경도 하나의 변경이라
   PR B도 proposal을 고칩니다: Technical Design의 Technical Decisions에 구현이
   설계에서 벗어난 곳을, Verification에 실제로 돌린 검사를 적습니다.
3. **마지막 PR이 머지되면 그 시점의 판단 기록입니다.** 이후 유일한 쓰기: 나중
   변경이 기록된 결정을 뒤집을 때, 그 변경의 PR이 옛 문서 제목 아래 blockquote로
   `Superseded in part by <문서>: <영향받는 결정>` 한 줄을 덧붙입니다. 나중에 알게
   된 것은 옛 문서를 고치지 않고 나중 proposal에 적습니다.
4. **PR 본문이 구현하는 proposal을 파일명으로 언급합니다.** 문서 쪽에는 PR 링크가
   없습니다. PR 번호는 문서를 다 쓴 뒤에야 생기기 때문입니다. 문서→PR 방향은
   `gh pr list --search "<파일명>"` 또는 `git log --follow`로 유도합니다.
5. 구현이 의도적으로 미뤄진 결정을 기록하는 Technical Design은 Summary에서 그렇다고
   밝힙니다. 머지된 Technical Design은 그렇지 않으면 배포된 것으로 읽힙니다.

왜 상태 필드가 없는가: 이전 형식은 작업 중 최신으로 유지해야 하는 필드(`Status`,
`Updated`, `Progress Log`, 진행 체크박스)를 요구했는데, 2026-07 시점에 `active/`의
문서 6개 중 5개가 이미 배포된 작업을 다루고 있었고, 그중 4개는 여전히
"In Progress"/"Review"로 표시된 채 방치돼 있었습니다. 유지 의무가 있는 필드는 반드시
썩습니다. 전체 근거는 `feature-decision-docs-process.md`에 있습니다.

## 이 저장소가 프랙티스 위에 얹는 것

- **Technical Design의 Design 끝에 `### Files touched` 목록**을 둡니다. 변경이 닿는
  서비스, 파일, 테스트를 적되 각 파일의 diff 내용은 적지 않습니다. `/deliver`가 이
  목록을 구현 체크리스트로 읽고, diff가 목록에서 벗어나면 Technical Decisions에
  기록합니다.
- **`DecisionDocsTests`**(`TarkovHelper.Tests/`)가 오프라인으로 검사하는 것: 날짜가
  붙은 문서의 이름 형태와 제목 접두사, 고정된 섹션 이름과 순서, 형식별 필수 섹션
  (Unified와 Product Requirements는 Problem/Goals/Non-Goals/Requirements, Technical
  Design은 Design/Test Strategy/Verification), `.requirements.md`와 `.design.md`의
  양방향 짝과 상호 링크, 이전 형식 `.spec.md`의 PRD 짝, `.ko.md`의 영어 원본, 추적
  파일에 적힌 `docs/decisions/` 경로의 해석, 그리고 어떤 새 형식 문서에도 유지해야
  하는 필드가 다시 들어오지 않았는지. 내용의 질과 supersede 노트를 잊지 않았는지는
  리뷰의 몫입니다.
- **글쓰기 관례**: 사람이 손으로 잘 치지 않는 문자(em dash, 가운뎃점, 한 글자
  줄임표)는 쓰지 않습니다. 화살표는 괜찮습니다.

## Change Proposal vs 참고 문서 vs 평가 문서

- **Change Proposal** (이 폴더): 한 변경의 의도와 판단. 그 시점의 기록이며 나중에
  고치지 않습니다.
- **참고 문서** (`docs/` 바로 아래, 또는 `TarkovDBEditor/docs/`): DB 스키마, 시스템
  분석, 로그 포맷 노트처럼 "현재 시스템이 어떻게 동작하는가"를 기술하는 살아있는
  문서. 시스템이 바뀌면 함께 고칩니다.
- **평가 문서** (`docs/assessments/`): 특정 커밋 시점의 코드베이스 평가 스냅샷.

## Folder Structure

```
docs/
├── decisions/
│   ├── README.md                          # 이 파일
│   ├── YYYY-MM-DD-<slug>.md               # Unified proposal
│   ├── YYYY-MM-DD-<slug>.requirements.md  # Split proposal의 Product Requirements
│   ├── YYYY-MM-DD-<slug>.design.md        # Split proposal의 Technical Design
│   ├── feature-*.md, fix-*.md, *.spec.md  # 2026-09-13 이전의 PRD와 spec (불변)
│   ├── templates/                         # change-proposal.md, product-requirements.md,
│   │                                      # technical-design.md
│   └── archive/                           # 동결된 레거시 문서 (YYYY-MM/, 형식과 위치 불변)
└── (그 외 모든 파일)                        # 참고 문서와 평가 문서 (docs/README.md 참고)
```

## Best Practices

1. **작은 단위**: proposal 하나가 다루는 변경은 독립적으로 배달할 수 있는 크기로.
   여러 PR에 걸치는 프로그램은 방향 문서 하나와 PR마다의 proposal로 나눕니다
   (`feature-eft-1-1-roadmap.md`가 그 예입니다).
2. **판별 가능한 기준**: `Requirements`의 각 항목은 참인지 거짓인지 판정할 수 있게
   씁니다. 전부 참이면 작업이 끝난 것입니다.
