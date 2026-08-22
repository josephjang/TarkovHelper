# TarkovHelper Database Schema

TarkovDBEditor에서 생성/관리하는 SQLite 데이터베이스 스키마 문서입니다.
TarkovHelper 앱에서 이 DB를 읽어 퀘스트, 아이템, 하이드아웃, 맵 마커 등의 데이터를 활용합니다.

---

## 목차

1. [System Tables](#system-tables)
2. [Item Tables](#item-tables)
3. [Quest Tables](#quest-tables)
4. [Hideout Tables](#hideout-tables)
5. [Map Tables](#map-tables)
6. [Trader Tables](#trader-tables)
7. [JSON Data Formats](#json-data-formats)
8. [Relationships Diagram](#relationships-diagram)

---

## System Tables

### _schema_meta

UI에서 테이블 컬럼 정보를 표시하기 위한 메타데이터 테이블입니다.

```sql
CREATE TABLE _schema_meta (
    TableName TEXT PRIMARY KEY,
    DisplayName TEXT,
    SchemaJson TEXT NOT NULL,      -- JSON array of ColumnSchema
    CreatedAt TEXT,                -- ISO 8601 format
    UpdatedAt TEXT                 -- ISO 8601 format
)
```

---

## Item Tables

### Items

게임 내 모든 아이템 정보를 저장합니다.

```sql
CREATE TABLE Items (
    Id TEXT PRIMARY KEY,           -- tarkov.dev ID 또는 생성된 ID (예: "dogtag-bear")
    BsgId TEXT,                    -- BSG 내부 ID
    Name TEXT NOT NULL,            -- 기본 이름
    NameEN TEXT,                   -- 영어 이름
    NameKO TEXT,                   -- 한국어 이름
    NameJA TEXT,                   -- 일본어 이름
    ShortNameEN TEXT,              -- 영어 약칭
    ShortNameKO TEXT,              -- 한국어 약칭
    ShortNameJA TEXT,              -- 일본어 약칭
    WikiPageLink TEXT,             -- Wiki 페이지 URL
    IconUrl TEXT,                  -- 아이콘 URL
    Category TEXT,                 -- 메인 카테고리
    Categories TEXT,               -- JSON array of categories
    IsDogtagItem INTEGER NOT NULL DEFAULT 0,  -- Dogtag 아이템 여부
    DogtagFaction TEXT,            -- Dogtag 진영: "BEAR", "USEC", 또는 NULL
    UpdatedAt TEXT                 -- 마지막 수정 시간
)
```

**특수 아이템 ID:**
- `dogtag-bear` - BEAR Dogtag (자동 생성)
- `dogtag-usec` - USEC Dogtag (자동 생성)

---

## Quest Tables

### Quests

퀘스트 기본 정보를 저장합니다.

```sql
CREATE TABLE Quests (
    Id TEXT PRIMARY KEY,           -- tarkov.dev ID
    BsgId TEXT,                    -- BSG 내부 ID
    Name TEXT NOT NULL,            -- 기본 이름
    NameEN TEXT,                   -- 영어 이름
    NameKO TEXT,                   -- 한국어 이름
    NameJA TEXT,                   -- 일본어 이름
    WikiPageLink TEXT,             -- Wiki 페이지 URL
    Trader TEXT,                   -- 트레이더 이름 (Prapor, Therapist 등)
    Location TEXT,                 -- 맵 이름 (Customs, Factory 등)

    -- 레벨 요구사항
    MinLevel INTEGER,              -- 최소 레벨
    MinLevelApproved INTEGER NOT NULL DEFAULT 0,
    MinLevelApprovedAt TEXT,

    -- 스캐브 카르마 요구사항
    MinScavKarma INTEGER,
    MinScavKarmaApproved INTEGER NOT NULL DEFAULT 0,
    MinScavKarmaApprovedAt TEXT,

    -- 카파 요구 여부
    KappaRequired INTEGER NOT NULL DEFAULT 0,  -- Wiki reqkappa 필드 기반

    -- 진영 제한
    Faction TEXT,                  -- "Bear", "Usec", 또는 NULL

    -- 에디션 제한
    RequiredEdition TEXT,          -- 필수 에디션: "EOD", "Unheard"
    RequiredEditionApproved INTEGER NOT NULL DEFAULT 0,
    RequiredEditionApprovedAt TEXT,
    ExcludedEdition TEXT,          -- 제외 에디션
    ExcludedEditionApproved INTEGER NOT NULL DEFAULT 0,
    ExcludedEditionApprovedAt TEXT,

    -- 복호화 횟수 요구사항
    RequiredDecodeCount INTEGER,
    RequiredDecodeCountApproved INTEGER NOT NULL DEFAULT 0,
    RequiredDecodeCountApprovedAt TEXT,

    -- 프레스티지 레벨 요구사항
    RequiredPrestigeLevel INTEGER,
    RequiredPrestigeLevelApproved INTEGER NOT NULL DEFAULT 0,
    RequiredPrestigeLevelApprovedAt TEXT,

    -- 진행 상황이 기록되는 키
    NormalizedName TEXT,

    -- 승인 상태
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT
)
-- Index: idx_quests_normalizedname (UNIQUE)
```

`Id`는 위키 페이지 URL의 base64입니다. 단, **퀘스트가 처음 등록될 때의 제목**으로
만들어진 값이며 이후 실행에서 현재 제목으로 다시 계산하지 않습니다. 패치 1.1이
퀘스트 91개의 이름을 바꾸고 제목 8개의 주인을 서로 맞바꾼 뒤로, 페이지 주소를 키로
쓰면 사용자가 기록한 진행 상황이 엉뚱한 퀘스트에 붙기 때문입니다. 이름이 바뀐
퀘스트는 `BsgId`(게임 내부 ID)로 인식해 기존 `Id`를 그대로 유지합니다.

`NormalizedName`은 앱이 이 컬럼이 없을 때 스스로 계산하던 식과 **정확히 같은 값**을
담습니다:

```sql
LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))
```

사용자의 퀘스트 진행 상황(`user_data.db`의 `QuestProgress.NormalizedName`)이 이 값으로
저장되어 있고, 설치된 모든 빌드는 이 컬럼이 생기는 순간 컬럼 값을 읽기 시작합니다.
따라서 tarkov.dev 스타일(`sew-it-good-part-4`)로 저장하면 퀘스트 228개의 진행 상황이
조용히 사라집니다. 이름이 바뀐 퀘스트는 **원래 제목**에서 나온 값을 그대로 유지하므로,
`NormalizedName`이 현재 `Name`과 일치하지 않을 수 있습니다(그것이 의도입니다).

### QuestRequirements

퀘스트 선행 조건 (어떤 퀘스트를 먼저 완료해야 하는지)을 저장합니다.

```sql
CREATE TABLE QuestRequirements (
    Id TEXT PRIMARY KEY,           -- "{QuestId}_{RequiredQuestId}" 형식
    QuestId TEXT NOT NULL,         -- 이 퀘스트
    RequiredQuestId TEXT NOT NULL, -- 선행 퀘스트
    RequirementType TEXT NOT NULL DEFAULT 'Complete',  -- Complete, Start 등
    DelayMinutes INTEGER,          -- 선행 퀘스트 완료 후 대기 시간 (분)
    GroupId INTEGER NOT NULL DEFAULT 0,  -- OR 그룹 (0 = AND, 같은 GroupId = OR)
    ContentHash TEXT,              -- 변경 감지용
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT,
    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
    FOREIGN KEY (RequiredQuestId) REFERENCES Quests(Id) ON DELETE CASCADE
)
-- Indexes: idx_questreq_questid, idx_questreq_requiredid
```

`RequirementType`은 앱이 아는 세 값(`Complete`, `Accept`, `Fail`)만 사용합니다. 설치된
빌드는 모르는 값을 "절대 충족되지 않음"으로 취급하므로, 다른 값이 들어가면 그 퀘스트는
영원히 잠깁니다.

### QuestTraderRequirements

퀘스트별 트레이더 충성도(Loyalty Level) 요구사항을 저장합니다.

```sql
CREATE TABLE QuestTraderRequirements (
    Id TEXT PRIMARY KEY,           -- "QTR|{QuestId}|{TraderId}"의 해시
    QuestId TEXT NOT NULL,
    TraderId TEXT NOT NULL,        -- tarkov.dev 트레이더 ID
    TraderName TEXT NOT NULL,      -- 표시용 이름 (Prapor, Jaeger 등)
    RequiredLevel INTEGER NOT NULL,-- 필요한 충성도 레벨
    ContentHash TEXT,              -- 변경 감지용
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT,
    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE
)
-- Index: idx_questtraderreq_questid
```

`Quests`에 컬럼 하나를 두지 않고 별도 테이블로 만든 이유는, 1.1에서 퀘스트 5개가
**자신을 주는 트레이더가 아닌** 트레이더의 충성도를 요구하기 때문입니다(Collector는
7명을 한꺼번에 요구합니다). 컬럼 하나였다면 그 조건들이 조용히 사라집니다.
`HideoutTraderRequirements`와 같은 모양입니다.

행은 `requirementType == "level"`인 조건만 담습니다. tarkov.dev는 평판(reputation)
조건도 같은 목록에 실어 보내지만 앱에는 그것을 표현할 자리가 없습니다.

### QuestObjectives

퀘스트 목표 (킬, 수집, 방문 등)를 저장합니다.

```sql
CREATE TABLE QuestObjectives (
    Id TEXT PRIMARY KEY,           -- NanoId로 생성
    QuestId TEXT NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    ObjectiveType TEXT NOT NULL DEFAULT 'Custom',
                                   -- Kill, Collect, HandOver, Visit, Mark, Stash, Survive, Build, Task
    Description TEXT NOT NULL,     -- 목표 설명
    TargetType TEXT,               -- Kill 목표: Scav, PMC, Boss, Any 등
    TargetCount INTEGER,           -- 필요 수량
    ItemId TEXT,                   -- Items 테이블 FK
    ItemName TEXT,                 -- 아이템 이름
    RequiresFIR INTEGER NOT NULL DEFAULT 0,  -- 발견 상태 필수 여부
    MapName TEXT,                  -- 특정 맵
    LocationName TEXT,             -- 맵 내 특정 위치
    LocationPoints TEXT,           -- JSON array of LocationPoint - 영역 정의
    OptionalPoints TEXT,           -- JSON array of LocationPoint - OR 위치들
    Conditions TEXT,               -- 추가 조건 텍스트
    DogtagMinLevel INTEGER,        -- Dogtag 최소 레벨
    DogtagFaction TEXT,            -- Dogtag 진영
    ContentHash TEXT,
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT,
    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
    FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE SET NULL
)
-- Indexes: idx_questobj_questid, idx_questobj_itemid, idx_questobj_map
```

### QuestRequiredItems

퀘스트에 필요한 아이템 (Wiki의 "Related Quest Items" 테이블 기반)을 저장합니다.

```sql
CREATE TABLE QuestRequiredItems (
    Id TEXT PRIMARY KEY,           -- Hash 기반 ID
    QuestId TEXT NOT NULL,
    ItemId TEXT,                   -- Items 테이블 FK (매칭된 경우)
    ItemName TEXT NOT NULL,        -- Wiki 아이템 이름
    Count INTEGER NOT NULL DEFAULT 1,  -- 필요 수량
    RequiresFIR INTEGER NOT NULL DEFAULT 0,  -- 발견 상태 필수 여부
    RequirementType TEXT NOT NULL DEFAULT 'Required',  -- Handover, Required, Optional
    SortOrder INTEGER NOT NULL DEFAULT 0,
    DogtagMinLevel INTEGER,        -- Dogtag 최소 레벨
    DogtagFaction TEXT,            -- Dogtag 진영
    ContentHash TEXT,
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT,
    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
    FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE SET NULL
)
-- Indexes: idx_questreqitem_questid, idx_questreqitem_itemid
```

### OptionalQuests

대체 퀘스트 관계 (퀘스트 A 또는 퀘스트 B가 선행 조건)를 저장합니다.

```sql
CREATE TABLE OptionalQuests (
    Id TEXT PRIMARY KEY,           -- "{QuestId}_{AlternativeQuestId}" 형식
    QuestId TEXT NOT NULL,
    AlternativeQuestId TEXT NOT NULL,
    ContentHash TEXT,
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,
    UpdatedAt TEXT,
    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
    FOREIGN KEY (AlternativeQuestId) REFERENCES Quests(Id) ON DELETE CASCADE
)
-- Indexes: idx_optquest_questid, idx_optquest_altid
```

---

## Hideout Tables

### HideoutStations

하이드아웃 스테이션/모듈 정보를 저장합니다.

```sql
CREATE TABLE HideoutStations (
    Id TEXT PRIMARY KEY,           -- tarkov.dev ID
    Name TEXT NOT NULL,            -- 영어 이름
    NameKO TEXT,                   -- 한국어 이름
    NameJA TEXT,                   -- 일본어 이름
    NormalizedName TEXT,           -- URL 친화적 이름
    ImageLink TEXT,                -- 아이콘 URL
    MaxLevel INTEGER NOT NULL DEFAULT 0,  -- 최대 레벨
    UpdatedAt TEXT
)
```

### HideoutLevels

각 스테이션의 레벨별 정보를 저장합니다.

```sql
CREATE TABLE HideoutLevels (
    Id TEXT PRIMARY KEY,           -- "{StationId}_{Level}" 형식
    StationId TEXT NOT NULL,
    Level INTEGER NOT NULL,
    ConstructionTime INTEGER NOT NULL DEFAULT 0,  -- 건설 시간 (초)
    UpdatedAt TEXT,
    FOREIGN KEY (StationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE
)
-- Index: idx_hideoutlevels_stationid
```

### HideoutItemRequirements

하이드아웃 레벨별 필요 아이템을 저장합니다.

```sql
CREATE TABLE HideoutItemRequirements (
    Id TEXT PRIMARY KEY,           -- "{StationId}_{Level}_{ItemId}" 형식
    StationId TEXT NOT NULL,
    Level INTEGER NOT NULL,
    ItemId TEXT NOT NULL,          -- tarkov.dev 아이템 ID
    ItemName TEXT NOT NULL,
    ItemNameKO TEXT,
    ItemNameJA TEXT,
    IconLink TEXT,
    Count INTEGER NOT NULL DEFAULT 1,
    FoundInRaid INTEGER NOT NULL DEFAULT 0,  -- FIR 필수 여부
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT,
    FOREIGN KEY (StationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE
)
-- Index: idx_hideoutitemreq_stationid
```

### HideoutStationRequirements

하이드아웃 레벨별 필요 선행 스테이션을 저장합니다.

```sql
CREATE TABLE HideoutStationRequirements (
    Id TEXT PRIMARY KEY,           -- "{StationId}_{Level}_{RequiredStationId}" 형식
    StationId TEXT NOT NULL,
    Level INTEGER NOT NULL,
    RequiredStationId TEXT NOT NULL,
    RequiredStationName TEXT NOT NULL,
    RequiredStationNameKO TEXT,
    RequiredStationNameJA TEXT,
    RequiredLevel INTEGER NOT NULL,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT,
    FOREIGN KEY (StationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE,
    FOREIGN KEY (RequiredStationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE
)
-- Index: idx_hideoutstationreq_stationid
```

### HideoutTraderRequirements

하이드아웃 레벨별 트레이더 충성도 요구사항을 저장합니다.

```sql
CREATE TABLE HideoutTraderRequirements (
    Id TEXT PRIMARY KEY,           -- "{StationId}_{Level}_{TraderId}" 형식
    StationId TEXT NOT NULL,
    Level INTEGER NOT NULL,
    TraderId TEXT NOT NULL,
    TraderName TEXT NOT NULL,
    TraderNameKO TEXT,
    TraderNameJA TEXT,
    RequiredLevel INTEGER NOT NULL,  -- 트레이더 충성도 레벨
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT,
    FOREIGN KEY (StationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE
)
-- Index: idx_hideouttraderreq_stationid
```

### HideoutSkillRequirements

하이드아웃 레벨별 스킬 요구사항을 저장합니다.

```sql
CREATE TABLE HideoutSkillRequirements (
    Id TEXT PRIMARY KEY,           -- "{StationId}_{Level}_{SkillName}" 형식
    StationId TEXT NOT NULL,
    Level INTEGER NOT NULL,
    SkillName TEXT NOT NULL,
    SkillNameKO TEXT,
    SkillNameJA TEXT,
    RequiredLevel INTEGER NOT NULL,  -- 스킬 레벨
    SortOrder INTEGER NOT NULL DEFAULT 0,
    UpdatedAt TEXT,
    FOREIGN KEY (StationId) REFERENCES HideoutStations(Id) ON DELETE CASCADE
)
-- Index: idx_hideoutskillreq_stationid
```

---

## Map Tables

### MapMarkers

맵 마커 (스폰, 탈출구, POI 등)를 저장합니다.

```sql
CREATE TABLE MapMarkers (
    Id TEXT PRIMARY KEY,           -- GUID
    Name TEXT NOT NULL,            -- 영어 이름
    NameKo TEXT,                   -- 한국어 이름
    MarkerType TEXT NOT NULL,      -- Enum: PmcSpawn, ScavSpawn, PmcExtraction,
                                   --       ScavExtraction, SharedExtraction, Transit,
                                   --       BossSpawn, RaiderSpawn, Lever, Keys
    MapKey TEXT NOT NULL,          -- 맵 키 (Customs, Factory 등)
    X REAL NOT NULL DEFAULT 0,     -- 게임 X 좌표
    Y REAL NOT NULL DEFAULT 0,     -- 게임 Y 좌표 (높이)
    Z REAL NOT NULL DEFAULT 0,     -- 게임 Z 좌표
    FloorId TEXT,                  -- 층 ID (main, basement, level2 등)
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
)
-- Indexes: idx_mapmarkers_mapkey, idx_mapmarkers_type
```

**MarkerType 값:**
| 값 | 설명 |
|---|---|
| `PmcSpawn` | PMC 스폰 지점 |
| `ScavSpawn` | Scav 스폰 지점 |
| `PmcExtraction` | PMC 전용 탈출구 |
| `ScavExtraction` | Scav 전용 탈출구 |
| `SharedExtraction` | 공용 탈출구 |
| `Transit` | 환승 지점 |
| `BossSpawn` | 보스 스폰 지점 |
| `RaiderSpawn` | 레이더 스폰 지점 |
| `Lever` | 레버/스위치 |
| `Keys` | 열쇠 위치 |

### MapFloorLocations

맵 층(Floor) 영역 정의를 저장합니다. Y좌표 범위로 자동 층 감지에 사용됩니다.

```sql
CREATE TABLE MapFloorLocations (
    Id TEXT PRIMARY KEY,
    MapKey TEXT NOT NULL,          -- 맵 키
    FloorId TEXT NOT NULL,         -- 층 ID
    RegionName TEXT NOT NULL,      -- 영역 이름
    MinY REAL NOT NULL,            -- Y 좌표 최소값
    MaxY REAL NOT NULL,            -- Y 좌표 최대값
    MinX REAL,                     -- X 좌표 최소값 (선택)
    MaxX REAL,                     -- X 좌표 최대값 (선택)
    MinZ REAL,                     -- Z 좌표 최소값 (선택)
    MaxZ REAL,                     -- Z 좌표 최대값 (선택)
    Priority INTEGER NOT NULL DEFAULT 0,  -- 우선순위 (높을수록 우선)
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
)
-- Index: idx_mapfloorlocations_mapkey
```

### ApiMarkers

Tarkov-market 등 외부 API에서 가져온 마커를 저장합니다.

```sql
CREATE TABLE ApiMarkers (
    Id TEXT PRIMARY KEY,
    TarkovMarketUid TEXT NOT NULL, -- 외부 API UID

    -- 마커 기본 정보
    Name TEXT NOT NULL,
    NameKo TEXT,
    Category TEXT NOT NULL,
    SubCategory TEXT,

    -- 위치 정보
    MapKey TEXT NOT NULL,
    X REAL NOT NULL,
    Y REAL,
    Z REAL NOT NULL,
    FloorId TEXT,

    -- 퀘스트 연관 정보
    QuestBsgId TEXT,               -- Quests 테이블 매칭용
    QuestNameEn TEXT,
    ObjectiveDescription TEXT,

    -- 메타 정보
    ImportedAt TEXT NOT NULL,
    IsApproved INTEGER NOT NULL DEFAULT 0,
    ApprovedAt TEXT,

    UNIQUE(TarkovMarketUid)
)
-- Indexes: idx_apimarkers_mapkey, idx_apimarkers_bsgid, idx_apimarkers_questname
```

---

## Trader Tables

### Traders

트레이더 정보를 저장합니다.

```sql
CREATE TABLE Traders (
    Id TEXT PRIMARY KEY,           -- tarkov.dev ID
    Name TEXT NOT NULL,            -- 영어 이름
    NameKO TEXT,                   -- 한국어 이름
    NameJA TEXT,                   -- 일본어 이름
    NormalizedName TEXT,           -- URL 친화적 이름
    ImageLink TEXT,                -- 아이콘 URL
    LocalIconPath TEXT,            -- 로컬 캐시 아이콘 경로
    UpdatedAt TEXT
)
```

---

## JSON Data Formats

### LocationPoints (QuestObjectives.LocationPoints)

퀘스트 목표 위치를 정의합니다.

```json
[
  {"X": 123.5, "Y": 0, "Z": -45.2, "FloorId": "main"},
  {"X": 125.0, "Y": 0, "Z": -43.8, "FloorId": "main"}
]
```

| 포인트 수 | 의미 |
|----------|------|
| 1개 | 단일 마커 |
| 2개 | 두 점 사이의 선 |
| 3개 이상 | 다각형 영역 |

### OptionalPoints (QuestObjectives.OptionalPoints)

대체 위치 (OR 관계)를 정의합니다.

```json
[
  {"X": 100.0, "Y": 0, "Z": -50.0, "FloorId": "main"},
  {"X": 200.0, "Y": 0, "Z": -60.0, "FloorId": "main"}
]
```

각 포인트는 독립적인 대체 위치입니다. UI에서 "OR1", "OR2" 등으로 표시됩니다.

### ColumnSchema (_schema_meta.SchemaJson)

테이블 컬럼 메타데이터를 정의합니다.

```json
[
  {
    "Name": "Id",
    "DisplayName": "ID",
    "Type": "Text",
    "IsPrimaryKey": true,
    "IsRequired": false,
    "IsAutoIncrement": false,
    "ForeignKeyTable": null,
    "ForeignKeyColumn": null,
    "SortOrder": 0
  }
]
```

**ColumnType 값:**
- `Text` - 문자열
- `Integer` - 정수
- `Real` - 실수
- `Boolean` - 불리언 (INTEGER 0/1로 저장)
- `DateTime` - 날짜/시간 (TEXT ISO 8601 형식)
- `Json` - JSON (TEXT로 저장)

---

## Relationships Diagram

```
┌─────────────────┐
│     Items       │
└────────┬────────┘
         │
         │ ItemId (FK)
         ▼
┌─────────────────┐     ┌──────────────────────┐
│     Quests      │◄────│   QuestRequirements  │
└────────┬────────┘     └──────────────────────┘
         │                        │
         │                        │ RequiredQuestId (FK)
         │                        ▼
         │              ┌──────────────────────┐
         │              │       Quests         │
         │              └──────────────────────┘
         │
         ├──────────────►┌──────────────────────┐
         │               │   QuestObjectives    │
         │               └──────────────────────┘
         │                        │
         │                        │ ItemId (FK)
         │                        ▼
         │               ┌─────────────────┐
         │               │     Items       │
         │               └─────────────────┘
         │
         ├──────────────►┌──────────────────────┐
         │               │  QuestRequiredItems  │
         │               └──────────────────────┘
         │
         ├──────────────►┌──────────────────────────┐
         │               │ QuestTraderRequirements  │
         │               └──────────────────────────┘
         │
         └──────────────►┌──────────────────────┐
                         │    OptionalQuests    │
                         └──────────────────────┘


┌──────────────────┐
│ HideoutStations  │
└────────┬─────────┘
         │
         ├──────────────►┌──────────────────────┐
         │               │    HideoutLevels     │
         │               └──────────────────────┘
         │
         ├──────────────►┌────────────────────────────┐
         │               │ HideoutItemRequirements    │
         │               └────────────────────────────┘
         │
         ├──────────────►┌────────────────────────────┐
         │               │ HideoutStationRequirements │
         │               └────────────────────────────┘
         │
         ├──────────────►┌────────────────────────────┐
         │               │ HideoutTraderRequirements  │
         │               └────────────────────────────┘
         │
         └──────────────►┌────────────────────────────┐
                         │ HideoutSkillRequirements   │
                         └────────────────────────────┘


┌─────────────────┐     ┌──────────────────────┐
│   MapMarkers    │     │   MapFloorLocations  │
└─────────────────┘     └──────────────────────┘
         │                        │
         │ MapKey                 │ MapKey
         ▼                        ▼
    [Map Config]             [Map Config]
```

---

## 사용 예시 (C#)

### 퀘스트와 목표 조회

```csharp
var sql = @"
    SELECT q.Id, q.NameKO, q.Trader, o.Description, o.MapName
    FROM Quests q
    LEFT JOIN QuestObjectives o ON q.Id = o.QuestId
    WHERE q.IsApproved = 1
    ORDER BY q.Name, o.SortOrder";

await using var connection = new SqliteConnection($"Data Source={dbPath}");
await connection.OpenAsync();
await using var cmd = new SqliteCommand(sql, connection);
await using var reader = await cmd.ExecuteReaderAsync();
```

### 맵별 마커 조회

```csharp
var sql = @"
    SELECT Id, Name, NameKo, MarkerType, X, Y, Z, FloorId
    FROM MapMarkers
    WHERE MapKey = @MapKey";

cmd.Parameters.AddWithValue("@MapKey", "Customs");
```

### 하이드아웃 요구사항 조회

```csharp
var sql = @"
    SELECT s.NameKO, l.Level, ir.ItemName, ir.Count
    FROM HideoutStations s
    JOIN HideoutLevels l ON s.Id = l.StationId
    JOIN HideoutItemRequirements ir ON s.Id = ir.StationId AND l.Level = ir.Level
    ORDER BY s.Name, l.Level, ir.SortOrder";
```

---

## 주의사항

1. **좌표 시스템**: MapMarkers의 X, Y, Z는 게임 좌표입니다. 화면 좌표로 변환하려면 `map_configs.json`의 `CalibratedTransform` 매트릭스를 사용해야 합니다.

2. **다국어 지원**: 대부분의 테이블에 `Name`, `NameKO`, `NameJA` 컬럼이 있습니다. 현재 언어 설정에 따라 적절한 컬럼을 사용하세요.

3. **승인 상태**: `IsApproved` 컬럼이 있는 테이블은 데이터 검증이 필요한 테이블입니다. 프로덕션에서는 승인된 데이터만 사용하세요.

4. **CASCADE 삭제**: 대부분의 FK는 `ON DELETE CASCADE`가 설정되어 있어, 부모 레코드 삭제 시 자식 레코드도 함께 삭제됩니다.
