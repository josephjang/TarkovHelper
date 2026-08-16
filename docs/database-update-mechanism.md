# TarkovHelper Database Update Mechanism

TarkovHelper의 데이터베이스 업데이트 메커니즘에 대한 상세 문서입니다.

---

## 개요

TarkovHelper는 두 가지 자동 업데이트 채널을 가집니다:

1. **앱 업데이트** — AutoUpdater.NET이 GitHub Release의 `TarkovHelper.zip`으로 앱 전체를 교체
   (`Services/UpdateService.cs`, 3분 주기 체크)
2. **DB 업데이트** — `Services/DatabaseUpdateService.cs`가 5분 주기로 GitHub raw의
   `db_version.txt`를 확인하고, 버전이 다르면 `tarkov_data.db`만 다운로드해 교체
   (앱 업데이트 없이 DB만 갱신됨). 폴링 대상은 그 빌드의 **데이터 채널**
   (`data/v<N>/`)이며, 자세한 내용은 아래 "데이터 채널" 절 참고

초기 배포 구조는 다음과 같습니다:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        TarkovDBEditor (별도 도구)                        │
│  ┌─────────────┐   ┌──────────────┐   ┌─────────────────────────────┐  │
│  │ tarkov.dev  │ → │  Wiki 캐시   │ → │  tarkov_data.db 생성/갱신   │  │
│  │    API      │   │   저장       │   │                             │  │
│  └─────────────┘   └──────────────┘   └─────────────────────────────┘  │
└────────────────────────────────────────────────────────┬────────────────┘
                                                         │
                                                         ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                           Release Package                                │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  TarkovHelper.zip                                                │   │
│  │  ├── TarkovHelper.exe                                           │   │
│  │  ├── Assets/                                                     │   │
│  │  │   ├── tarkov_data.db  ← 번들된 마스터 데이터                  │   │
│  │  │   └── db_version.txt                                          │   │
│  │  └── ...                                                         │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────┬────────────────┘
                                                         │
                                              AutoUpdater.NET
                                                         │
                                                         ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                           TarkovHelper (사용자 PC)                       │
│  ┌─────────────────────┐     ┌─────────────────────────────────────┐   │
│  │   tarkov_data.db    │     │        user_data.db                  │   │
│  │   (읽기 전용)        │     │        (사용자 진행상황)             │   │
│  │   - Items           │     │        - QuestProgress               │   │
│  │   - Quests          │     │        - HideoutProgress             │   │
│  │   - MapMarkers      │     │        - ItemInventory               │   │
│  │   - Hideout         │     │        - UserSettings                │   │
│  └─────────────────────┘     └─────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 데이터 흐름

### 1. TarkovDBEditor에서 데이터 수집

TarkovDBEditor는 다음 소스에서 데이터를 수집합니다:

#### tarkov.dev GraphQL API
```graphql
# 아이템 데이터
query Items($lang: LanguageCode!) {
  items(lang: $lang) {
    id, name, normalizedName, shortName
    iconLink, wikiLink, category { ... }
  }
}

# 퀘스트 데이터
query Tasks($lang: LanguageCode!) {
  tasks(lang: $lang) {
    id, name, normalizedName, trader { ... }
    objectives { ... }, requirements { ... }
  }
}

# 하이드아웃 데이터
query HideoutStations($lang: LanguageCode!) {
  hideoutStations(lang: $lang) {
    id, name, levels { ... }
  }
}
```

#### Wiki 데이터 캐싱
- `TarkovDBEditor/Services/WikiCacheService.cs`: Wiki 페이지 캐싱
- `TarkovDBEditor/Services/WikiQuestService.cs`: Wiki 퀘스트 파싱
- 캐시 위치: `TarkovDBEditor/wiki_data/`

### 2. DB 파일 생성/업데이트

**핵심 서비스**: `TarkovDBEditor/Services/RefreshDataService.cs`

```csharp
public async Task<RefreshResult> RefreshDataFromCacheAsync(
    string databasePath,
    TarkovDevDataService? tarkovDevService = null,
    WikiCacheService? wikiCacheService = null,
    Action<string>? progress = null,
    CancellationToken cancellationToken = default)
{
    // 1. 기존 DB에서 Items 로드
    // 2. 캐시된 Quests 로드
    // 3. DB 업데이트 (Quests, Requirements, Objectives 등)
    // 4. Traders 업데이트
}
```

### 3. 앱 릴리즈 패키징

**TarkovHelper.csproj** 설정:
```xml
<None Update="Assets\tarkov_data.db">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

**build/Create-ReleasePackage.ps1** (릴리즈 워크플로가 실행):
```powershell
# dotnet publish (framework-dependent) → artifacts/TarkovHelper.zip 패키징
# tarkov_data.db와 db_version.txt가 포함됨
```

### 4. AutoUpdater.NET 앱 업데이트

**update.xml** (repo 루트, raw main에서 서빙) — 아래는 **첫 릴리즈 후 예시** 형태입니다.
현재 커밋된 값은 `4.3.1`이며, `update.xml`은 릴리즈 자산이 존재한 뒤에야 범프됩니다
(의도된 lag; `.claude/commands/release.md` 7단계 참조):
```xml
<?xml version="1.0" encoding="UTF-8"?>
<item>
    <version>2026.7.0</version>
    <url>https://github.com/josephjang/TarkovHelper/releases/download/v2026.7.0/TarkovHelper.zip</url>
    <changelog>https://github.com/josephjang/TarkovHelper/releases/latest</changelog>
    <mandatory>false</mandatory>
</item>
```

**Services/UpdateService.cs** (업데이트 URL과 체크 로직의 소유자):
```csharp
internal const string UpdateXmlUrl = "https://raw.githubusercontent.com/josephjang/TarkovHelper/main/update.xml";

// 3분 주기 + 수동 버튼으로 update.xml 체크; 새 버전 발견 시 UI에 표시.
// 사용자가 업데이트 버튼을 누르면 AutoUpdater.Start(UpdateXmlUrl)로 교체 수행.
```

### 5. DB 자동 업데이트 (DatabaseUpdateService)

**Services/DatabaseUpdateService.cs** — 앱 업데이트와 독립적으로 DB만 갱신:
```csharp
// csproj의 <TarkovDataFormat>에서 파생 (AssemblyMetadata 경유). 하드코딩된 상수가 아님
internal static readonly string VERSION_URL  = ".../refs/heads/main/data/v1/db_version.txt";
internal static readonly string DATABASE_URL = ".../refs/heads/main/data/v1/tarkov_data.db";

// 5분 주기로 원격 db_version.txt를 읽어 첫 줄 토큰을 로컬과 문자열 비교;
// 다르면 tarkov_data.db를 .tmp로 내려받아 교체하고 DatabaseUpdated 이벤트 발생
```

> **참고 (앱 업데이트와 DB의 상호작용):** 앱 self-update zip에는 릴리즈 시점의
> `tarkov_data.db`/`db_version.txt` 스냅샷이 포함되므로, DB 자동 업데이트로 더 최신 DB를
>받은 사용자가 앱을 업데이트하면 DB가 스냅샷 버전으로 잠시 되돌아갑니다. 다만 시작 시
> `StartBackgroundUpdates`가 즉시(dueTime 0) 체크하여 원격과 다르면 곧바로 다시 내려받아
> 자기 치유되므로 stale 구간은 수 초에 그칩니다 — 의도된 동작입니다.

---

## 데이터베이스 구조

### Master Data (tarkov_data.db) - 읽기 전용

| 테이블 | 용도 |
|--------|------|
| `Items` | 게임 아이템 정보 |
| `Quests` | 퀘스트 기본 정보 |
| `QuestRequirements` | 퀘스트 선행 조건 |
| `QuestObjectives` | 퀘스트 목표 |
| `QuestRequiredItems` | 퀘스트 필요 아이템 |
| `HideoutStations` | 하이드아웃 스테이션 |
| `HideoutLevels` | 하이드아웃 레벨별 정보 |
| `HideoutItemRequirements` | 하이드아웃 필요 아이템 |
| `MapMarkers` | 맵 마커 (탈출구, 스폰 등) |
| `MapFloorLocations` | 맵 층 정의 |
| `Traders` | 트레이더 정보 |

### User Data (user_data.db) - 읽기/쓰기

| 테이블 | 용도 |
|--------|------|
| `QuestProgress` | 퀘스트 완료 상태 |
| `ObjectiveProgress` | 목표별 완료 상태 |
| `HideoutProgress` | 하이드아웃 건설 진행 |
| `ItemInventory` | 보유 아이템 (FIR/Non-FIR) |
| `UserSettings` | 앱 설정 |

---

## 데이터 채널 (버전 인식 업데이트 경로)

설계 문서: `docs/decisions/feature-versioned-data-channel.spec.md`

### 데이터 형식(format)과 엔드포인트

**데이터 형식**은 앱 빌드가 읽을 수 있는 `tarkov_data.db`의 계약(스키마 + 값의 의미)입니다.
추가 전용 변경(새 컬럼/테이블, `ColumnExistsAsync`로 feature-detect)은 형식을 올리지 않고,
기존 리더를 깨뜨리는 변경(rename, 용도 변경, 삭제, 의미 변경)만 형식을 올립니다.

```
<repo>/
├── data/
│   └── v1/                        # 형식 1 엔드포인트 (앱이 폴링하는 곳)
│       ├── tarkov_data.db
│       └── db_version.txt
└── TarkovHelper/Assets/           # 채널 이전 빌드가 폴링하는 주소 (형식 1의 두 번째 주소)
    ├── tarkov_data.db             # data/v1과 항상 바이트 단위로 동일
    └── db_version.txt
```

`TarkovHelper/Assets/`는 이미 배포된 빌드가 URL을 하드코딩하고 있어 변경할 수 없으므로
남겨 둡니다. 두 주소는 같은 형식의 엔드포인트이므로 **항상 함께 갱신되고 함께 동결**되며,
`DataChannelMirrorTests`가 바이트 동일성을 CI에서 강제합니다.

### 형식 고정(pin)

`TarkovHelper.csproj`의 `<TarkovDataFormat>` 하나가 두 가지를 동시에 결정합니다:

1. 빌드에 번들되는 시드 DB (`data/v$(TarkovDataFormat)/`에서 `Assets\`로 복사)
2. `DatabaseUpdateService`가 폴링하는 URL (`AssemblyMetadata` 경유)

따라서 번들 데이터와 폴링 채널이 어긋날 수 없습니다. 메타데이터가 없거나 잘못되면
`DatabaseUpdateService`는 기본값으로 넘어가지 않고 즉시 예외를 던집니다.

### 형식 올리기와 동결(freeze)

추가 전용으로 만들 수 없는 publish가 처음 필요해질 때:

1. 새 형식 디렉터리 `data/v<N+1>/`를 만들고 새 데이터를 넣습니다 (도구가 아니라 사람이,
   앱의 pin을 올리는 같은 PR에서).
2. 이하 형식 엔드포인트에는 더 이상 쓰지 않습니다.
3. 각 이하 형식의 `db_version.txt`에 `frozen` 줄을 덧붙입니다. 버전 토큰과 DB는 그대로 둡니다.

동결된 채널을 폴링하는 빌드는 토큰이 그대로이므로 다운로드하지 않고, 타이틀 바에
"데이터 업데이트 종료" 알림을 표시합니다. 채널 이전 빌드는 문자열 전체를 비교하므로
변경으로 보고 동일한 DB를 한 번 다시 받습니다 (무해, 1회).

### db_version.txt 형식

첫 번째 비어 있지 않은 줄이 **버전 토큰**이고, 그 뒤의 각 줄은 **디렉티브**입니다.

```
1.0.10          <- 버전 토큰 (기존과 동일한 문자열 동등 비교 대상)
frozen          <- 디렉티브 (현재 정의된 유일한 값)
```

- 알 수 없는 디렉티브는 의도적으로 무시합니다. 이미 배포된 빌드는 새 어휘를 배울 수 없으므로,
  엔드포인트가 새 빌드에게만 새로운 이야기를 할 수 있어야 합니다.
- 토큰이 없는 본문(빈 파일 등)은 실패한 체크로 처리합니다 (다운로드 없음, 로컬 상태 변경 없음).
- 로컬 `Assets/db_version.txt`에는 토큰만 기록합니다. 동결 여부는 데이터가 아니라
  엔드포인트의 상태이므로 매 체크마다 다시 읽습니다.

## 버전 관리

### DB 버전
- 파일: `data/v<N>/db_version.txt`와 그 미러 `Assets/db_version.txt` (예: `1.0.10`,
  앱 버전과 독립적인 DB 데이터 버전)
- `DatabaseUpdateService`가 로컬/원격 **토큰**을 문자열 동등 비교하여 다르면 DB를 다운로드
  (순서 비교가 아니므로, 이전 내용을 새 토큰으로 다시 publish하면 롤백이 됩니다)
- 배포 zip에 포함됨 (없으면 신규 설치가 첫 체크에서 DB 전체를 재다운로드)

### 앱 버전 변경 시 처리

```csharp
// App.xaml.cs
private void CheckAndRefreshDataOnVersionChange()
{
    var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    var savedVersion = GetSavedVersion();

    if (savedVersion != currentVersion)
    {
        // 캐시 데이터 삭제 (user_data.db는 보존)
        DeleteCacheDataFiles();
        SaveCurrentVersion(currentVersion);
    }
}
```

---

## Map 데이터 소스

### MapMarkers 테이블

v3.5.0에서 tarkov.dev API를 사용하던 것이 현재는 DB 테이블로 변경됨:

```sql
SELECT Id, Name, NameKo, MarkerType, MapKey, X, Y, Z, FloorId
FROM MapMarkers
WHERE MapKey = @MapKey
```

**MarkerType 종류**:
- `PmcSpawn` - PMC 스폰 지점
- `ScavSpawn` - Scav 스폰 지점
- `PmcExtraction` - PMC 탈출구
- `ScavExtraction` - Scav 탈출구
- `SharedExtraction` - 공용 탈출구
- `Transit` - 환승 지점
- `BossSpawn` - 보스 스폰
- `Lever` - 레버

### QuestObjectives 테이블 (위치 포함)

```sql
SELECT Id, QuestId, ObjectiveType, Description, MapName,
       LocationPoints, OptionalPoints
FROM QuestObjectives
WHERE MapName = @MapName
```

**LocationPoints JSON 형식**:
```json
[{"X": 123.5, "Y": 0, "Z": -45.2, "FloorId": "main"}]
```

### map_configs.json

맵별 좌표 변환 설정 (Assets/DB/Data/map_configs.json):

```json
{
  "maps": [
    {
      "key": "Customs",
      "displayName": "Customs",
      "svgFileName": "Customs.svg",
      "calibratedTransform": [...],
      "playerMarkerTransform": [...],
      "floors": [
        {"layerId": "main", "displayName": "Ground Floor", "order": 0}
      ]
    }
  ]
}
```

---

## 업데이트 워크플로우

### 개발자 워크플로우

**DB만 갱신** (앱 릴리즈 불필요):

```
1. TarkovDBEditor 실행 → "Refresh Data"
   - tarkov.dev API에서 최신 데이터 가져옴
   - Wiki 데이터 캐시 업데이트
   - tarkov_data.db 업데이트
2. Map Editor에서 마커 편집 (필요시)
3. Data Publish 창에서 publish
   - DB와 db_version.txt를 live 형식(`data/v<N>/`, 저장소에 있는 가장 높은 v 디렉터리)에 씀
   - 형식이 1이면 `TarkovHelper/Assets/`에도 같은 바이트를 미러링
   - 아이콘/맵/설정은 기존대로 Assets/에만 (앱 릴리즈로 배포되는 자산)
4. **복사된 엔드포인트 파일을 한 커밋에 함께** main에 커밋/push
   → 사용자 앱의 DatabaseUpdateService가 5분 내 자동 반영
```

> publish 도구는 새 형식 디렉터리를 만들지 않습니다. 형식을 올리는 것은 앱의 pin을 함께
> 올리는 리뷰된 PR에서만 하는 의도적인 행위이며, 일상적인 publish가 실수로 형식을 바꿀 수
> 없게 하기 위한 것입니다.

**앱 릴리즈** (`/release <version>` 커맨드, 상세는 `.claude/commands/release.md`):

```
1. csproj 버전 범프 커밋 → v<version> 태그 push
2. GitHub Actions(release.yml)가 빌드/테스트/패키징 → Release + TarkovHelper.zip 생성
3. 릴리즈 노트 큐레이션
4. 자산 확인 후 update.xml 범프 (마지막 — 클라이언트가 404 URL을 보지 않도록)
```

### 사용자 워크플로우

```
1. TarkovHelper 시작
2. AutoUpdater가 update.xml 체크
3. 새 버전 발견 시:
   - 다운로드 확인 대화상자 표시
   - TarkovHelper.zip 다운로드
   - 자동 설치 및 재시작
4. 새 tarkov_data.db가 자동으로 포함됨
```

---

## 관련 파일

### TarkovHelper
- `Services/UpdateService.cs` - 앱 업데이트 체크 (update.xml URL 소유), AutoUpdater 실행
- `Services/DatabaseUpdateService.cs` - DB 자동 업데이트 (db_version.txt/tarkov_data.db URL 소유)
- `App.xaml.cs` - 앱 버전 변경 시 캐시 초기화 (`CheckAndRefreshDataOnVersionChange`)
- `Services/UserDataDbService.cs` - 사용자 데이터 관리
- `Services/MigrationService.cs` - 버전 마이그레이션

### TarkovDBEditor
- `Services/RefreshDataService.cs` - 데이터 새로고침
- `Services/TarkovDevDataService.cs` - API 연동
- `Services/WikiCacheService.cs` - Wiki 캐싱
- `Services/MapMarkerService.cs` - 맵 마커 관리
- `Services/DatabaseService.cs` - DB 코어 서비스

### 설정 파일
- `update.xml` - AutoUpdater 설정
- `data/v<N>/db_version.txt` - DB 버전 (엔드포인트 원본)
- `Assets/db_version.txt` - 형식 1 미러 겸 설치본의 로컬 버전 기록
- `Assets/DB/Data/map_configs.json` - 맵 설정
- `TarkovHelper.csproj`의 `<TarkovDataFormat>` - 시드 DB와 폴링 URL을 함께 결정하는 pin

---

## 향후 개선 가능성

1. **증분 업데이트**: 변경된 데이터만 다운로드 (현재는 DB 파일 전체 교체)

(별도 DB 업데이트, db_version.txt 버전 체크, 백그라운드 동기화는
`DatabaseUpdateService`로 구현 완료)
