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
- [2026-08-game-mode-selector-ux-review.html](2026-08-game-mode-selector-ux-review.html): 활성
  프로필 선택 UI의 UX 검토와 인터랙티브 개선안
- [2026-08-profile-data-attribution-code-guide.html](2026-08-profile-data-attribution-code-guide.html):
  진행도 귀속(attribution) 설계를 배경부터 따라가는 코드 가이드. 세션 모드 타임라인,
  싱크 분배, 프로필 전환 경합을 직접 조작해 보는 인터랙티브 랩 3개와, 이해도를
  검증하는 퀴즈 게이트를 포함합니다 (PR #38)
- [2026-08-profile-data-attribution-deep-review-guide.html](2026-08-profile-data-attribution-deep-review-guide.html):
  코드 가이드 2부. 전부 통과한 테스트 스위트가 왜 아무것도 증명하지 못했는지,
  딥 리뷰가 찾은 구현 결함들을 다룹니다. 로그 읽기 상한, 로캘 의존 날짜 파싱,
  키 철자 불일치, 유실된 쓰기를 조작해 보는 인터랙티브 랩 4개와 퀴즈 게이트를
  포함합니다 (PR #38)
- [2026-08-complete-profile-reset-code-guide.html](2026-08-complete-profile-reset-code-guide.html):
  코드 가이드 3부. 프로필 완전 초기화(SPA-3/4/6)의 설계를 따라갑니다. 단일
  트랜잭션 리셋과 롤백, 진행 중 쓰기를 배수하는 배리어, 로그 재유입을 막는
  워터마크 펜스를 직접 조작해 보는 인터랙티브 랩 3개와 퀴즈 게이트를 포함합니다
  (PR #41)
- [2026-08-complete-profile-reset-deep-review-guide.html](2026-08-complete-profile-reset-deep-review-guide.html):
  코드 가이드 4부. 프로필 완전 초기화 딥 리뷰가 찾은 결함들과 수정을 다룹니다.
  멈춘 쓰기와 배수 타임아웃, 리셋이 볼 수 없던 대기 중 저장, 첫 실행 마이그레이션
  경합을 조작해 보는 인터랙티브 랩 3개와 퀴즈 게이트를 포함합니다 (PR #42)
- [2026-08-reset-dialog-ux-review-guide.html](2026-08-reset-dialog-ux-review-guide.html):
  코드 가이드 5부. 초기화 다이얼로그 UX 리뷰가 찾은 세 가지 개선(키보드 기본
  포커스, 대상 프로필 이름 강조, 테마 브러시 통일)을 다룹니다. 키보드 상태
  머신과 포맷 슬롯 렌더러를 조작해 보는 인터랙티브 랩 2개와 퀴즈 게이트를
  포함합니다 (PR #41)
- [2026-08-profile-settings-race-code-guide.html](2026-08-profile-settings-race-code-guide.html):
  코드 가이드 6부. 프로필 설정 캐시가 전환 가드 없이 여덟 번의 개별 읽기로
  채워지던 문제(SPA-2)와 스냅샷 기반 수정을 다룹니다. 여덟 번 읽기 사이로
  전환이 끼어드는 순간, 리비전 게이트가 뒤늦은 리로드를 버리는 과정, 편집이
  어느 프로필에 저장되는지를 조작해 보는 인터랙티브 랩 3개와 퀴즈 게이트를
  포함합니다 (PR #43)
- [2026-08-profile-settings-race-deep-review-guide.html](2026-08-profile-settings-race-deep-review-guide.html):
  코드 가이드 7부. 프로필 설정 경합 수정(6부)을 딥 리뷰가 다시 훑어 찾은 결함들과
  수정을 다룹니다. 실패한 로드가 체크박스 에코를 타고 저장된 Edge of Darkness
  플래그를 지우던 연쇄, 퍼블리시 게이트 밖에서 돌던 일곱 이벤트 팬아웃, 카운터
  하나로는 설명할 수 없던 편집 구간, 손으로 고친 행을 그대로 받아들이던 대량
  읽기를 조작해 보는 인터랙티브 랩 4개와 퀴즈 게이트를 포함합니다 (PR #46)
- [2026-08-versioned-data-channel-code-guide.html](2026-08-versioned-data-channel-code-guide.html):
  tarkov_data.db 자동 업데이트에 데이터 포맷 버전별 엔드포인트와 다운로드 검증을
  도입한 변경을 다룹니다. 매니페스트의 크기와 해시, 그리고 user_version 스탬프가
  설치 직전에 무엇을 걸러내는지, index.json 하나로 빌드가 뒤에 남았음을 알아내는
  과정, 두 주소에 동시에 발행하며 미러 드리프트를 고치는 퍼블리시를 조작해 보는
  인터랙티브 랩 3개와 퀴즈 게이트를 포함합니다 (PR #48)
- [2026-08-versioned-data-channel-deep-review-guide.html](2026-08-versioned-data-channel-deep-review-guide.html):
  바로 위 코드 가이드가 다룬 데이터 채널을 딥 리뷰가 다시 훑어 찾은, 조용히 실패하던
  경로들과 수정을 다룹니다. 읽기 전용 속성 하나 때문에 매시간 6.9 MB를 다시 받으면서
  영영 설치를 끝내지 못하던 파일 교체, 구독자 하나가 발견된 업데이트를 실패로 바꿔
  버리던 체크, 앱이 거부하는 채널을 정상이라고 보고하던 퍼블리셔, 스스로 지워 버려서
  한 번밖에 실패하지 못하던 스키마 드리프트 가드를 조작해 보는 인터랙티브 랩 4개와
  퀴즈 게이트를 포함합니다 (PR #49)
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
- [assessments/2026-08-quest-data-1-1-refresh-grounds.md](assessments/2026-08-quest-data-1-1-refresh-grounds.md):
  EFT 1.1 로드맵 3단계(퀘스트 데이터 리프레시) 결정 문서가 근거로 삼는 검증된
  사실과 증거, 리뷰에서 반박된 주장 목록 (26개 finding)

## 관례

- **파일명은 kebab-case**로 짓습니다 (`database-schema.md`).
- **새 참고 문서와 평가 문서는 영어로** 씁니다. 기존 한국어 문서는 그대로
  유지합니다 (결정 문서와 같은 규칙).
- 새 문서를 추가하면 해당 목록에도 한 줄 추가합니다.
