# docs/

저장소 전체의 문서가 모이는 곳입니다. 세 계층으로 나뉩니다:

- **결정 문서** (`decisions/`): 할 작업(또는 한 작업)에 대한 결정 기록.
  형식과 규칙은 `decisions/README.md` 참고.
- **참고 문서** (이 폴더 바로 아래): 현재 시스템이 어떻게 동작하는가를 기술하는
  살아있는 문서. 시스템이 바뀌면 함께 고칩니다.
- **평가 문서** (`assessments/`): 특정 커밋 시점에 코드베이스 전반을 평가한
  스냅샷. 파일명에 연월을 포함하고(`2026-08-code-health.md`), 머지되면
  동결됩니다. 개별 지적 사항(finding)의 처리 상태는 문서가 아니라 GitHub
  PR/이슈가 소유하며, 고치는 PR 본문이 finding ID(예: `THR-1`)를 언급합니다.

단일 작업에 딸린 시점 분석은 참고 문서로 두지 않고 해당 작업 spec의
Current Behavior 섹션으로 남깁니다. 코드베이스 전반을 훑는 시점 평가만
`assessments/`에 들어갑니다. 과거의 스냅샷들은 `decisions/archive/`에
동결되어 있습니다.

프로젝트 내부에만 해당하는 구현 노트는 각 프로젝트의 `docs/`에 둡니다
(예: `TarkovDBEditor/docs/`).

## 참고 문서 목록

- [database-schema.md](database-schema.md): tarkov_data.db 스키마
  (TarkovDBEditor가 생성, TarkovHelper가 소비)
- [database-update-mechanism.md](database-update-mechanism.md): 앱과 DB의
  자동 업데이트 메커니즘
- [eft-1-1-profile-selection-log-analysis.md](eft-1-1-profile-selection-log-analysis.md):
  EFT 1.1의 시즌/영구/PvE 프로필 전환 로그 캡처와 파서 결론
- [eft-log-patterns.md](eft-log-patterns.md): EFT 게임 로그 폴더 구조와
  레이드 정보 추출 패턴
- [eft-live-log-capture-runbook.md](eft-live-log-capture-runbook.md): 현재 EFT
  클라이언트를 실행하고 비식별 로그 증거를 수집하는 Windows 절차
- [eft-raid-event-service.md](eft-raid-event-service.md): EftRaidEventService가
  제공하는 이벤트와 사용법
- [tarkov-market-markers-api.md](tarkov-market-markers-api.md): Tarkov Market
  마커 API 분석

## 평가 문서 목록

- [assessments/2026-08-code-health.md](assessments/2026-08-code-health.md):
  TarkovHelper 앱과 솔루션 툴링 전반의 코드 품질 평가 (34개 finding)
- [assessments/2026-08-seasonal-profile-amplified-issues.md](assessments/2026-08-seasonal-profile-amplified-issues.md):
  Seasonal Profile 추가로 영향이 커지는 기존 문제와 후속 작업 경계 (6개 finding)
- [assessments/2026-08-seasonal-profile-adjacent-issues.md](assessments/2026-08-seasonal-profile-adjacent-issues.md):
  Seasonal Profile 분석에서 확인했지만 영향이 특별히 커지지 않는 인접 문제
  (4개 finding)

## 관례

- **파일명은 kebab-case**로 짓습니다 (`database-schema.md`).
- **새 참고 문서와 평가 문서는 영어로** 씁니다. 기존 한국어 문서는 그대로
  유지합니다 (결정 문서와 같은 규칙).
- 새 문서를 추가하면 해당 목록에도 한 줄 추가합니다.
