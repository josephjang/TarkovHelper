# Smart Quest Recommendations PRD

> Superseded in part by `2026-09-22-remove-quest-recommendations.md` (2026-09-22):
> the panel this document specified is removed. Under the EFT 1.1 quest data three
> of its five scoring signals no longer tell quests apart, so it is not re-scored;
> a 1.1-aware replacement gets its own proposal.

## 기능 개요

플레이어의 현재 상태(레벨, 보유 아이템, 퀘스트 진행도)를 분석하여 "다음에 할 퀘스트"를 자동으로 추천해주는 시스템.

## 문제 정의

타르코프에는 500개 이상의 퀘스트가 있고, 복잡한 선행 퀘스트 의존성이 있음. 신규/복귀 유저들이:
- 어떤 퀘스트를 먼저 해야 하는지 모름
- 이미 필요 아이템을 보유하고 있는 퀘스트를 놓침
- 카파 컨테이너 진행에 비효율적인 경로를 선택함

## 해결책

### 5가지 추천 타입

| 타입 | 색상 | 설명 | 우선순위 |
|------|------|------|----------|
| **Ready to Complete** | 🟢 Green | 모든 필요 아이템을 보유한 퀘스트 | 100+ |
| **Item Hand-in Only** | 🔵 Blue | 아이템 제출만 필요 (복잡한 목표 없음) | 50-100 |
| **Kappa Priority** | 🟣 Purple | 카파 컨테이너 필수 퀘스트 | 70-90 |
| **Unlocks Many** | 🟠 Orange | 2개 이상 후속 퀘스트 해금 | 60-80 |
| **Easy Quest** | 🔵 Cyan | 아이템 필요 없는 쉬운 퀘스트 | 40 |

### 우선순위 계산 공식

```
Priority = BaseScore + KappaBonus + UnlockBonus + FulfillmentBonus

BaseScore:
- ReadyToComplete: 100
- ItemHandInOnly: 50
- KappaPriority: 70
- UnlocksMany: 60
- EasyQuest: 40

KappaBonus: +10~20 (카파 필수 퀘스트)
UnlockBonus: +5 × (후속 퀘스트 수)
FulfillmentBonus: +40 × (아이템 충족 비율) [ItemHandInOnly 전용]
```

## 구현 상세

### 새로 추가된 파일

#### `Services/QuestRecommendationService.cs`
- 싱글톤 서비스
- `GetRecommendations(int maxResults)` - 상위 N개 추천 반환
- `GetReadyToCompleteQuests()` - 즉시 완료 가능 퀘스트
- `GetItemHandInOnlyQuests()` - 아이템 제출만 필요한 퀘스트
- `GetKappaPriorityQuests()` - 카파 필수 퀘스트
- `GetHighImpactQuests()` - 다수 해금 퀘스트
- 아이템 보유량 분석 (`ItemInventoryService` 연동)

### 수정된 파일

#### `Services/LocalizationService.cs`
- 추천 관련 다국어 문자열 추가 (EN/KO/JA)
  - `RecommendedQuests`, `ReadyToComplete`, `ItemHandInOnly`
  - `KappaPriority`, `UnlocksMany`, `EasyQuest`
  - `NoRecommendations`, `ShowMore`, `ShowLess`

#### `Pages/QuestListPage.xaml`
- Grid Row 추가 (3 → 4행)
- `RecommendationsExpander` - 접을 수 있는 추천 섹션
- `RecommendationsList` - 추천 퀘스트 목록 (ItemsControl)
- 추천 아이템 템플릿: 타입 배지, 퀘스트명, 이유, 카파 배지, 트레이더 아이콘

#### `Pages/QuestListPage.xaml.cs`
- `RecommendationViewModel` 클래스 추가
- 추천 타입별 브러시 정의
- `UpdateRecommendations()` - 추천 목록 갱신
- `Recommendation_Click()` - 클릭 시 퀘스트 선택 및 상세 표시

## UI 동작

1. **Quests 탭 로드 시**: 자동으로 추천 계산 및 표시
2. **퀘스트 완료/리셋 시**: 추천 목록 자동 업데이트
3. **추천 클릭 시**: 해당 퀘스트로 자동 이동 및 선택
4. **추천이 없을 때**: 섹션 숨김

## 진행 상황

| 단계 | 상태 | 날짜 | 비고 |
|------|------|------|------|
| 기능 설계 | ✅ 완료 | 2025-12-08 | 5가지 추천 타입 정의 |
| QuestRecommendationService | ✅ 완료 | 2025-12-08 | 핵심 추천 로직 구현 |
| LocalizationService | ✅ 완료 | 2025-12-08 | EN/KO/JA 문자열 추가 |
| QuestListPage XAML | ✅ 완료 | 2025-12-08 | 추천 섹션 UI 추가 |
| QuestListPage Code-behind | ✅ 완료 | 2025-12-08 | 추천 표시 로직 |
| 빌드 검증 | ✅ 완료 | 2025-12-08 | 오류 0개 |

## 변경 파일 목록

| 파일 | 변경 유형 |
|------|----------|
| `Services/QuestRecommendationService.cs` | 신규 |
| `Services/LocalizationService.cs` | 수정 (70줄 추가) |
| `Pages/QuestListPage.xaml` | 수정 (100줄 추가) |
| `Pages/QuestListPage.xaml.cs` | 수정 (100줄 추가) |

## 테스트 방법

1. 앱 실행 → Quests 탭 이동
2. **예상 결과:**
   - "Recommended Quests" 섹션이 표시됨
   - 최대 5개의 추천 퀘스트 표시
   - 각 추천에 타입 배지와 이유 표시
3. 추천 퀘스트 클릭 → 해당 퀘스트가 선택됨
4. 퀘스트 완료 → 추천 목록 자동 업데이트
5. Items 탭에서 아이템 보유량 변경 → 추천 반영 (앱 재시작 필요)

## 추후 개선 가능 사항

1. **실시간 아이템 반영**: Items 탭에서 수량 변경 시 즉시 추천 업데이트
2. **맵 기반 추천**: 특정 맵에서 완료 가능한 퀘스트 추천
3. **레이드 플래너 연동**: 맵 트래커와 연동한 추천
4. **카파 최단 경로**: 카파 컨테이너까지 최적 경로 계산
5. **트레이더 레벨업 추천**: 특정 트레이더 레벨업에 필요한 퀘스트 추천
