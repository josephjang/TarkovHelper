# TarkovHelper

[![en](https://img.shields.io/badge/lang-English-blue.svg)](README.md)
[![ko](https://img.shields.io/badge/lang-한국어-red.svg)](README.ko.md)
[![ja](https://img.shields.io/badge/lang-日本語-green.svg)](README.ja.md)
[![Latest release](https://img.shields.io/github/v/release/josephjang/TarkovHelper)](https://github.com/josephjang/TarkovHelper/releases/latest)

Escape from Tarkov의 퀘스트, 은신처, 아이템 진행 상황을 추적하고, 게임이 남기는 로그 파일을 감시해 자동으로 동기화하는 Windows 데스크톱 도우미입니다.

> **참고**: 이 저장소는 [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper)에서 갈라져 나와 독립적으로 유지보수되는 포크입니다. CalVer(`YYYY.M.N`) 버전 체계로 자체 릴리즈를 배포하며(**v2026.7.0**부터 시작), 기능을 계속 추가하고 있습니다.

![Tarkov Helper의 퀘스트 추적](screenshots/quests_ko.png)

## 다운로드

[최신 릴리즈](https://github.com/josephjang/TarkovHelper/releases/latest)에서 **TarkovHelper.zip**을 받아 원하는 곳에 풀고 `TarkovHelper.exe`를 실행하세요.

- **Windows** 및 [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 필요
- 실행 시 **관리자 권한 상승**을 요청합니다. 이유는 [작동 방식과 안전성](#작동-방식과-안전성)을 참고하세요.

설치 후에는 앱과 게임 데이터가 모두 자동으로 최신 상태로 유지됩니다.

## 주요 기능

- **퀘스트**: 모든 퀘스트 조회/검색, 상태/트레이더/맵/Kappa/진영별 필터, 목표와 선행/후속 퀘스트 확인, 다음에 할 퀘스트 추천
- **은신처**: 시설 레벨을 추적하고 각 업그레이드에 필요한 아이템, 트레이더, 스킬, 연계 시설 확인
- **아이템**: 퀘스트와 은신처 업그레이드에 아직 필요한 모든 아이템을 하나의 목록으로 집계, FIR(Found in Raid)/일반 아이템을 보유 수량과 함께 구분 추적
- **수집가**: Collector 퀘스트 아이템 전용 체크리스트와 아직 남은 해금 조건
- **맵**: 퀘스트 마커와 탈출구가 표시되는 인터랙티브 맵, 레이드 중 위치 추적 포함
- **오버레이 미니맵**: 게임 중 사용할 수 있는 항상 위 미니맵, 전역 단축키로 제어
- **게임 로그 동기화**: 퀘스트 시작/완료/실패 상태와 게임 모드를 게임 로그 파일에서 자동 인식
- **PvP/PvE 프로필**: 모드별로 분리된 진행 상황, 플레이 중인 모드에 맞춰 자동 전환
- **자동 업데이트**: 앱과 게임 데이터베이스가 백그라운드에서 자동 갱신
- **3개 언어**: English, 한국어, 日本語, 앱 내에서 전환 가능

## 작동 방식과 안전성

Tarkov Helper는 모든 게임 상태를 **게임이 직접 기록하는 파일을 읽는 수동적인 방식**으로만 얻습니다:

- **로그 파일**: 퀘스트와 레이드 이벤트, 게임 모드는 게임 자체 로그에서 읽습니다
- **스크린샷 파일명**: 레이드 중 위치는 게임의 스크린샷 기능에서 얻습니다. 게임이 파일명에 좌표를 기록하기 때문입니다

게임 메모리를 읽거나, 코드를 주입하거나, 게임 파일을 수정하지 **않습니다**. 오버레이 미니맵은 평범한 항상-위(always-on-top) 창이며, 전역 단축키는 Tarkov Helper 자체 프로세스에서 동작하는 시스템 전역 키보드 훅을 사용합니다. 이 훅과 로그 파일 접근이 실행 시 관리자 권한을 요청하는 이유입니다.

어떤 서드파티 도구도 Battlestate Games를 대신해 보장할 수는 없으므로, 사용 여부는 본인의 판단에 맡깁니다.

## 시작하기

### 게임 로그 동기화

동기화는 별도 설정 없이 동작합니다. 앱이 Tarkov 설치 위치(BSG 런처 및 Steam)를 자동 감지해 로그 감시를 시작합니다. 설치 위치를 찾지 못하면 **설정** → **Tarkov 로그 폴더**에서 **자동 감지** 또는 **찾아보기...**로 게임의 `Logs` 폴더를 지정하세요.

### 맵 위치 추적

레이드 중 **맵** 탭에서 내 위치를 갱신하려면 게임의 스크린샷 키를 누르세요. 앱이 새 스크린샷의 파일명에서 좌표를 읽습니다. 스크린샷을 찍지 않으면 위치는 저절로 갱신되지 않습니다.

### 진행 상황 저장 위치

진행 상황은 `TarkovHelper.exe` 옆의 `Config` 폴더에 저장됩니다. 설치 위치마다 데이터를 따로 보관하므로, 앱을 새 위치로 옮긴 뒤 진행 상황이 비어 보이면 **설정** → **Data Migration**에서 이전 위치의 데이터를 가져오세요. 게임 데이터(퀘스트, 아이템, 은신처)는 앱에 내장되어 있고 자동으로 갱신되므로 수동으로 받아올 것이 없습니다.

## 스크린샷 더 보기

![필요 아이템 집계](screenshots/items_ko.png)
![은신처 업그레이드 추적](screenshots/hideout_ko.png)

## 소스에서 빌드

[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)가 필요합니다.

```powershell
git clone https://github.com/josephjang/TarkovHelper.git
cd TarkovHelper
dotnet build TarkovHelper/TarkovHelper.csproj -c Release
```

그다음 `TarkovHelper\bin\Release\net8.0-windows\TarkovHelper.exe`를 실행하고 권한 상승 프롬프트를 승인하세요. (앱 매니페스트가 관리자 권한을 요구하기 때문에, 권한 상승 없는 터미널에서는 `dotnet run`이 동작하지 않습니다.)

## 라이선스

[MIT License](LICENSE)

`TarkovHelper/Fonts/` 아래에 번들된 폰트는 서드파티 저작물이며 MIT 라이선스의
적용 대상이 **아닙니다**. Play와 Noto Sans CJK KR은 SIL Open Font License 1.1,
Bender는 `TarkovHelper/Fonts/LICENSE-Bender.txt`의 출처 고지에 따릅니다. 각
`Fonts/LICENSE-*.txt` 파일에서 해당 조건을 확인하세요.

## 크레딧

- 원본 프로젝트: [Zeliper/Tarkov-Item-Helper](https://github.com/Zeliper/Tarkov-Item-Helper)
- 게임 데이터: [tarkov.dev](https://tarkov.dev)
- Escape from Tarkov는 Battlestate Games의 상표입니다.
- 폰트: Bender (Jovanny Lemonad / TypeType), Play (OFL), Noto Sans CJK KR (OFL)
