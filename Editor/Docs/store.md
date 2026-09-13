# 스토어 등록 — 붙여넣기만 하면 되는 것들

**업로드는 사람이 한다.** AI는 여기까지 준비하고 멈춘다 (`CLAUDE.md` §5).
이 문서는 업로드 폼 앞에서 글을 쓰지 않아도 되게 만드는 것이 목적이다.

---

## 먼저 알아야 할 것 — SDK가 실제로 보내는 것은 적다

디컴파일로 확인한 사실이다. 폼에 칸이 보여도 코드가 안 읽는 것이 있다.

| 대상 | SDK가 보내는 것 | 나머지는 |
| --- | --- | --- |
| **mod.io** | 이름 · **한 줄 요약** · 로고 이미지 · 공개 여부 | 긴 설명·태그·의존성은 **mod.io 웹사이트에서** |
| **창작마당** | 제목 · 설명 · 태그 · 공개 범위 · 썸네일 · 파일 ID | — |

- mod.io 업로드에는 **버전도 변경 로그도 안 실린다.** SDK가 그 칸을 비워 보낸다
- mod.io 폼의 `URL`·`Tags` 입력칸은 **화면에 없고 코드도 안 읽는다**
- 새 mod.io 프로필은 **비공개로 생성된다.** 웹에서 공개로 바꿔야 보인다
- 창작마당 공개 범위를 안 건드리면 **비공개가 기본값이다**

## ⚠ 먼저 해야 하는 한 가지

**모드 SDK 창의 `Mod Management` 탭에서 `Build Mod`(또는 `Build and Install Mod`)를 한 번 눌러야 한다.**

우리 빌드는 `Editor/build.ps1` → `CliBuild` → `ModBuilder.BuildMod`로 직접 들어가는데, 이 경로는
SDK의 `ModPaths.asset`에 경로를 등록하지 않는다. 지금 그 목록이 비어 있어서 **창작마당 탭이
"No built mod found"로 막힌다.** GUI로 한 번 빌드하면 등록되고 그 뒤로는 풀린다.

---

## 창작마당 (Steam Workshop)

**설명문은 이미 저장소에 있다.** 모드 루트의 `description.txt`를 SDK가 자동으로 읽는다
(GUI 설명 칸을 비워두면 그 파일이 쓰인다). 고칠 일이 있으면 그 파일을 고친다.

절차:

1. 스팀을 켜둔다 → `PugMod → Open Mod SDK Window` → `Steam Workshop` 탭 → `Initialize Steam`
2. `Select a Built Mod`에서 NoBreakZone
3. **Title:** `No Break Zone`
4. **Description:** 비워둔다 (`description.txt`가 쓰인다)
5. **File ID:** 새로 올리는 것이면 `0` 또는 공란. 갱신이면 기존 ID
6. **Mod Visibility:** 처음에는 `Private`로 올려 확인한 뒤 `Public`으로 바꾸는 편이 안전하다
7. **Tags** — 종류별로 하나씩 추가한다

   | Tag Type | 고를 것 |
   | --- | --- |
   | Category | `Quality of Life`, `Item` |
   | AppType | `Client`, `Server` |
   | AccessType | **`Script (Elevated Access)`** |

   > 마지막 것은 선택이 아니다. 우리 설정이 `accessesExtraAssemblies: 1`이라 그 태그가 맞는
   > 표기다. 기획서 §12의 "배포 시 서버 측 모드로 표기한다"는 AppType에 `Server`를 넣는 것으로
   > 지킨다 — 다만 클라이언트도 오브젝트를 그려야 하므로 `Client`도 함께 넣는다

8. **Select Thumbnail for Mod** → 아래 이미지
9. `Upload to Steam Workshop` → 돌아오는 **published file ID를 기록**한다
   (`Editor/NoBreakZone_Steam.asset`에도 저장된다)

> **제목이 빌드 산출물을 덮어쓴다.** Title 값이 빌드된 폴더의 `ModManifest.json`의 `displayName`에
> 써진다. 다시 빌드하면 원래 값으로 돌아가므로, 업로드 직전에 빌드하고 바로 올린다.

---

## mod.io

1. SDK 창의 `Mod.IO` 버튼으로 로그인 (이메일 → 보안 코드). 로그인해야 업로드 탭이 나타난다
2. `Select a Built Mod`에서 NoBreakZone
3. **Description** (여기 칸은 mod.io의 **한 줄 요약**으로 간다) — 아래 요약문
4. **Select Thumbnail for Mod.io** → 아래 이미지
5. `Register at mod.io` → 프로필이 **비공개로** 생성된다
6. `Upload to mod.io` → 프로젝트에서 임시 폴더로 다시 빌드해 올린다
7. `Go to mod page` → **웹사이트에서** 긴 설명·태그·의존성·공개 전환을 한다

---

## 붙여넣을 글

### mod.io 요약 — 영어

업로드 폼의 `Description` 칸에 들어가는 것이 이것이다. 긴 설명이 아니라 **한 문단짜리 요약**이고,
mod.io가 목록에서 모드 이름 밑에 보여준다.

```
Place a pylon, press E, and everything in the square around it stops being destructible — chests, walls, floors, machines. Press E again and your base is ordinary, so you can remodel it.
```

### mod.io 요약 — 한국어

```
파일런을 설치하고 E를 누르면 그 주변 사각형 안의 모든 것이 부서지지 않습니다. 상자도, 벽도, 바닥도, 기계도 마찬가지입니다. 다시 E를 누르면 평소대로 돌아가 기지를 고칠 수 있습니다.
```

### 긴 설명 (영어)

모드 루트의 [`description.txt`](../../description.txt) 내용을 그대로 쓴다. 창작마당은 그 파일을
자동으로 읽고, mod.io 웹사이트에는 사람이 붙여넣는다.

### 긴 설명 (한국어)

```
내 곡괭이, 내 폭탄, 빗나간 공격에 기지가 부서지는 것을 막습니다.

파일런을 설치하고 E를 누르면 그 주변 사각형 안의 모든 것이 부서지지 않습니다. 상자도, 벽도,
바닥도, 기계도, 파일런 자신도 마찬가지입니다. 다시 E를 누르면 평소대로 돌아가 기지를 고칠 수
있습니다. 보호는 전역도 영구도 아닙니다. 내가 고른 자리에서, 내가 켜는 것입니다.

[무엇이 들어 있나]

  파일런         설치하고 E. 켜짐 상태는 저장하고 불러와도 유지됩니다.
  파일런 작업대   아래 셋을 만드는 곳. 철 작업대에서 제작합니다.
  파일런 렌즈     들고 있으면 켜져 있는 모든 파일런의 경계가 보입니다.
                 그 외에는 어떤 방법으로도 범위가 표시되지 않습니다.
  파일런 리모콘   멀리 떨어진 파일런을 켜고 끕니다. 벽 너머로 막혔을 때를 위한 것입니다.

[켜져 있는 동안]

범위 안의 어떤 것도 부술 수 없습니다. 벽과 광석도 포함입니다. 이것은 실수가 아니라 의도입니다.
실수로 날려먹을 수 없는 기지는 실수로 고칠 수도 없는 기지이므로, 파내고 싶을 때는 파일런을
끄면 됩니다.

농사와 드릴 자동화는 예외라서 켜둔 채로도 정상 작동합니다. 부서질 때가 아니라 맞을 때마다
자원을 내놓는 것들, 특히 드릴이 캐는 광석 덩어리는 절대 보호하지 않습니다. 그것을 보호하면
자원이 무한 복제되어 세이브가 영구히 망가지기 때문입니다. 이 규칙이 이 모드의 다른 무엇보다
우선합니다.

[멀티플레이]

무엇이 보호되는지, 스위치가 무엇을 하는지는 서버가 결정하므로 모든 플레이어에게 동일하게
보입니다. 소유권 개념은 없습니다. 누가 설치했든 파일런은 그 안의 것을 보호합니다.

[설정]

  protectionDiameter  21     파일런 하나가 덮는 사각형의 한 변 길이(타일)
  blockMobDamage      true   끄면 몹과 폭발은 보호된 것을 부술 수 있게 됩니다
  showRangeWithLens   true   렌즈를 들었을 때 경계를 표시합니다
  remoteReachTiles    30     리모콘의 사거리

소스와 문의: https://github.com/LeeShinYeoung/no-break-zone
MIT 라이선스.
```

---

## 이미지

두 스토어 모두 **`.png` / `.jpg`만** 받는다. SDK는 형식만 검사하고 크기는 안 본다.
mod.io가 서버에서 **1280×720 · 640×360 · 320×180**으로 다시 렌더하므로 **16:9**가 맞고,
**1280×720 이상**으로 준비한다. 너무 크면 mod.io가 거부한다(`ModLogoTooLarge`).

`Editor/Docs/art/store_thumbnail.png`가 그 규격으로 생성되어 있다.
`Editor/Docs/art/sprites.py`가 만들며, 다시 뽑으려면 그 스크립트를 실행한다.

---

## 올린 뒤에

- mod.io 웹에서 **공개로 전환**하고 긴 설명·태그를 채운다
- 창작마당 공개 범위를 `Public`으로 올린다
- 돌아온 **ID 두 개(창작마당 file ID, mod.io mod ID)를 이 문서에 적어둔다.** 다음 갱신 때 필요하다

| 스토어 | ID | 주소 |
| --- | --- | --- |
| 창작마당 | *(미등록)* | |
| mod.io | *(미등록)* | |
