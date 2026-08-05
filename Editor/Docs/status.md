# 진행상황

최종 갱신: 2026-08-05 (기획서 3단계 코드 완료. 파일런 에셋이 생겼고 하드코딩 좌표가 사라짐)

> **단계 번호 주의.** 이 문서가 예전에 쓰던 "1~4단계"는 저장소 자체 번호였다.
> 이제 **기획서(`design.md`) 13장 번호로 통일**한다. 옛 4단계 = 기획서 1단계다.

---

## 현재 상태

**기획서 1단계(피해 차단) 검증 완료. 2·3단계 코드 완료 — 미검증.**

| 기획서 단계 | 상태 |
| --- | --- |
| 1 피해 차단 | ✅ 인게임 검증 완료 |
| **2 좌표 조건** | **코드 완료·미검증** (`feat/pylon-implementation`) |
| **3 파일런 커스텀 오브젝트** | **코드 완료·미검증** (같은 브랜치) — 📍 체크포인트 1 |
| 4 토글·세이브 / 5 시각 / 6 부가 / 7 배포 | 미착수 |

1단계 핵심 해결: 클라이언트 예측 오작동이 원인이었고, 게임 자체 플래그 `IndestructibleCD`를
서버+클라 양쪽 월드에서 켜서 해결. 상세·교훈은 `research.md` 9장.

2단계에서 한 것:

- `Scripts/`를 `Logic/`(게임 타입 비의존) · `Components/` · `Systems/`로 나눔
- 보호 범위를 21×21 정사각형 안으로 좁힘. **파일런 좌표는 아직 하드코딩** (3단계에서 대체)
- 태그 두 개 도입 — `NoBreakZoneEvaluatedCD`(매 프레임 월드 훑기 방지),
  `NoBreakZoneProtectedCD`(원래 무적이던 오브젝트를 4단계에서 잘못 되돌리지 않기 위해)
- `Editor/Tests/` — 범위 판정 + 보호 판별. 후자는 `object_flags.csv` 2282개 전수 회귀(569개 보호)
- `Editor/preflight.py` — 맥에서 돌리는 정적 검증 (아래)

### 이번 세션의 가장 큰 소득 — 조사에 윈도우가 필요 없어졌다

게임 전체 디컴파일 소스·공식 문서·최신 레퍼런스 모드가 전부 공개 저장소에 있다.
**게임 DLL 복사도, 디컴파일러 설치도 불필요.** 목록과 검증 근거는 `research.md` 10장.

그리고 **유니티 없이 프리팹을 쓸 수 있다는 것**을 실증했다 — `m_Script` fileID가 클래스
이름에서 계산된다(레퍼런스 184건 100% 일치). 레시피는 `research.md` 11장,
참조표는 `GameData/script_fileids.csv`.

윈도우에 남는 일은 **빌드와 인게임 검증** 둘뿐이다.

3단계에서 한 것:

- **`Editor/genassets.py`** — 에셋 생성기. 파일런 에셋 9종(프리팹 2, SpriteAsset, TextDataBlock,
  텍스처+임포트, 매니페스트, `.meta`들)을 스펙에서 찍는다. YAML을 손으로 안 쓴다.
  6단계의 렌즈·리모콘·작업대는 `SPECS` 리스트에 항목을 더하면 된다
- **guid·fileID·`m_address`를 전부 에셋 경로에서 해시로 유도한다.** 재실행해도 안 바뀌므로
  `--check`가 통과한다. 참조가 끊기는 사고(CLAUDE.md §1-2)를 구조적으로 막는다
- `Scripts/Systems/NoBreakZonePylonRegistrySystem.cs` — 실제 설치된 파일런을 찾아 좌표를 공급.
  `NoBreakZoneProtectionSystem`의 하드코딩 좌표가 사라졌다
- 파일런을 놓으면 **이미 판정이 끝난 오브젝트를 다시 판정한다.** 안 그러면 나중에 놓은 파일런이
  원래 있던 상자를 보호하지 못한다

### 맥에서 돌리는 검증

```
python3 Editor/preflight.py        # 프리팹 참조·금지 네임스페이스·.meta 짝·asmdef
python3 Editor/genassets.py --check # 생성된 에셋이 스펙과 일치하는지
```

프리팹 `m_Script` 역산 · 금지 네임스페이스 · `.meta` 짝 · asmdef 참조를 본다.
**컴파일 검증이 아니다** — 타입 오류는 윈도우 빌드에서만 드러난다.

---

## 다음에 할 일

### 📍 체크포인트 1 — 사람이 윈도우에서 확인 (다음 작업)

**4단계 이후가 전부 이 프리팹 위에 쌓인다.** 여기서 한 번 봐두면 나중에 되돌릴 범위가 좁아진다.

```
유니티 에디터 닫기 → powershell -File Editor/build.ps1   → 종료 코드 0
유니티 Test Runner → NoBreakZone.Tests
게임 재시작 → Player.log 에서 [NoBreakZone] loaded (0.0.1-stage3)
```

인게임에서 볼 것, 중요한 순서대로:

1. **자원 복제가 없는가** — 드릴로 광석이 정상 고갈, 곡괭이로 벽·광석 정상 채굴, 작물 수확 정상.
   기획서 §6이 다른 무엇보다 우선한다고 못박은 항목이다
2. **파일런이 나오고 설치되는가** — 아직 제작할 수 없다(레시피를 거는 작업대가 6단계다).
   콘솔로 꺼내야 한다
3. **보이는가** — 스프라이트가 안 보이면 `m_address` 가정을 제일 먼저 의심한다
   (`research.md` 11장). **2×2 타일 크기로 보이는 것이 정상이다** — 아래 참조
4. **범위가 맞는가** — 파일런 기준 10타일 안 상자는 안 부서지고 **밖 상자는 부서지는지**
5. **나중에 놓은 파일런도 듣는가** — 상자를 먼저 놓고 그 옆에 파일런을 설치.
   `[NoBreakZone] pylons changed` 로그가 뜨고 그 상자가 보호되면 재판정이 도는 것이다
6. **파일런을 다시 회수할 수 있는가** — 3단계에서는 파일런이 자기 자신을 보호하지 않게 해뒀다.
   4단계에서 반대가 된다

### ⚠️ 스프라이트 결정 (a)의 근거가 틀렸다 (2026-08-05)

**`spritePixelsToUnits`로는 월드 크기를 못 줄인다.** 32px 텍스처는 무슨 값을 넣든 **2×2 타일로
렌더된다** — `SpriteObject.PixelsPerUnit`이 코드에 박힌 `16f`이고, 월드 렌더 경로는 Sprite
서브에셋을 아예 거치지 않는다. 근거는 `research.md` 11장.

*조치*는 그대로 둔다(32로 유지). 그 값이 실제로 먹는 곳은 인벤토리 아이콘이고 32px 텍스처에는
그게 맞는 값이다. 바뀐 것은 **기대치**뿐이다.

결정 (a)의 원래 논지 — "임시 스프라이트고 아트는 7단계 마감 항목이니 인게임에서 눈으로 본 뒤
고친다" — 는 그대로 살아 있다. 위 체크포인트에서 **크기를 보고** 판단하면 된다.

줄이는 쪽으로 갈 경우의 비용도 재봤다. 초안은 16px 그림의 2배 확대가 **아니다**(2×2 블록이
단색인 비율 73%). 색이 11개뿐이라 축소해도 형태는 읽히지만 무손실은 아니다.
줄이기로 하면 `genassets.py`의 `art`·`pixels_to_units` 두 줄을 고치고 재실행하면 끝이다.

확인된 것: **그레이스케일은 필수가 아니다.** 레퍼런스 모드는 컬러로 직접 그린다.
그레이스케일 + GradientMap은 스킨(색 교체)용 선택 기능이다.

### 파일런을 어떻게 꺼내는가 — 미확인

3단계 시점에 파일런은 **제작할 수 없다.** 레시피는 `InventoryItemAuthoring`에 적혀 있지만
그것을 만들어 주는 `CraftingAuthoring`이 아직 없다(6단계 작업대). 체크포인트 1에서는 게임 콘솔로
꺼내야 하는데 **정확한 명령을 아직 확인하지 않았다.** 게임에 QuantumConsole이 들어 있는 것은
확인됨(`NoBreakZone.asmdef`의 `QFSW.QC.dll`). 오브젝트 이름은 `NoBreakZone.Pylon`이다.

### 4단계 — 토글·세이브 (그다음 작업)

3단계가 자리를 다 만들어 뒀다. 고칠 곳이 코드 주석에 표시돼 있다.

| 할 일 | 어디 |
| --- | --- |
| `variationIsDynamic: 1`, `variationToToggleTo: 1` | `genassets.py`의 `ObjectAuthoring` 블록 |
| 켜짐(변형 1) 스프라이트 | `pylon_on.png` → `SpriteAsset.m_staticVariants` |
| E키 토글 | 그래픽 프리팹의 `interactable` 슬롯 + `InteractWithEnvironmentSystem` |
| 켜진 파일런만 수집 | `NoBreakZonePylonRegistrySystem.CollectPositions()` 에 `variation == 1` |
| 파일런 자체 무적 | 보호 시스템에서 파일런을 빼둔 `None` 조건을 걷어내고 변형에 연동 |
| **보호 해제** | 파일런이 꺼지면 `NoBreakZoneProtectedCD`가 붙은 것만 되돌린다. 3단계는 붙이기만 한다 |

마지막 줄이 3단계에서 남긴 유일한 기능적 구멍이다. 지금은 파일런을 없애도 보호가 안 풀린다.

### 5단계 착수 전 — 반드시 물어볼 것

**범위 표시에 재사용할 바닐라 구현이 없다.** 기획서 §7은 "게임에 이미 있는 방식을 따른다
(아이템 수집기 등)"고 하는데 게임 소스에 그런 컴포넌트가 없다. 레퍼런스 모드도 `SpriteRenderer`를
직접 풀링한다. 기획서 §7의 대안 조항으로 가야 하므로 **착수 전 승인**이 필요하다
(`research.md` 10장).

### 저장소 구조

단일 모드로 확정하고 커뮤니티 표준 레이아웃에 맞췄다. 근거와 레퍼런스는 `workflow.md` 11장.
`.cs`는 전부 `Scripts/` 아래이고, 그 안이 다시 `Logic/`·`Components/`·`Systems/`로 나뉜다.
3단계에서 `Prefabs/`·`Textures/`·`Data/SpriteAsset/`·`Data/TextDataBlock/Items/`가 붙었고
`SpriteAssetManifest.asset`은 레퍼런스 모드와 같이 모드 루트에 둔다.
문서·게임데이터·테스트·`preflight.py`·`genassets.py`는 `Editor/` 아래라 번들에 안 실린다.

**`Prefabs/`·`Textures/`·`Data/`의 에셋은 손으로 고치지 않는다.** `genassets.py`가 소유하고
매번 덮어쓴다. 바꾸려면 스펙을 고치고 재실행한다. (폴더 `.meta`만은 예외 — 한 번 만들고 다시
안 건드린다. guid가 바뀌면 그 아래 참조가 전부 끊긴다)

### 윈도우에서 처리해야 할 것 — ModBuilderSettings 추적 (미완)

**빌드 설정 에셋이 저장소 밖에 있어 버전 관리가 안 된다.** 모드 이름·의존성·`modPath`·
Linux 빌드 여부가 전부 윈도우 머신에만 있고, 그 머신이 사라지면 복원할 근거가 없다.

근거: `Data/NoBreakZone.asset`에는 `metadata`·`modPath` 필드가 없다(= 런타임 데이터 에셋이지
빌드 설정이 아니다). `CliBuild.cs`는 `Assets/NoBreakZone.asset`을 로드하고 빌드는 성공하므로,
그 파일은 저장소 밖 `Assets/` 바로 아래에 존재한다. 레퍼런스 모드는 4/4 모두 이 파일을
모드 폴더 안에 두어 추적한다.

윈도우에서 할 일:

1. 유니티 프로젝트 창에서 `Assets/NoBreakZone.asset`을 `Assets/NoBreakZone/` 안으로 드래그
   (`.meta`는 유니티가 같이 옮긴다)
2. 그 에셋의 `modPath`가 `Assets/NoBreakZone` 그대로인지 인스펙터에서 확인
3. `Editor/CliBuild.cs`의 `ModSettingsAssetPath` 상수를
   `"Assets/NoBreakZone/NoBreakZone.asset"` 으로 수정
4. `Editor/build.ps1` 실행해 종료 코드 0 확인 → 커밋

### 함께 확인할 것 (윈도우, 미검증)

- 이번 구조 정리(`Scripts/` 이동, 폴더 개명) 후 유니티가 **재임포트·GUID 경고 없이** 여는지
- 빌드 종료 코드 0 — `asmdef`가 하위 폴더를 포괄하는지 확인하는 실질 테스트
- 루트 `README.md`·`LICENSE.md`·`CLAUDE.md` 때문에 `Player.log`에 새 `couldn't load` 경고가
  뜨는지. 뜨면 루트 파일을 더 줄이는 결정이 필요하다
- Unity 버전 불일치 — 공식 SDK README는 `6000.0.58f2`, `build.ps1`은 `6000.0.59f2`

---

_(아래는 이전 단계 기록)_

**1·2·3단계 완료. 4단계 코드 작성·컴파일·빌드까지 됐으나 인게임 검증은 아직 안 함(=코드 완료, 검증 완료 아님). 다음: 사람이 게임에서 테스트.**

- **3단계(게임 코드 조사) 완료** — 파괴 파이프라인 전체를 디컴파일로 규명. `research.md` 8장. 핵심: 모든 피해가 `SetEntitiesDestroyedSystem` 단일 관문을 지나며, `DontDestroyOnZeroHealthCD{disabled=false}` 부여로 파괴를 막는다. 전부 Burst라 데이터 레벨 개입만 가능.
- **4단계(하드코딩 피해 차단) 코드 완료** — 서버 시스템 `NoBreakZoneProtectionSystem`이 `PlaceablePrefab`+`DamageableObjectCD` 설치물에 보호 컴포넌트 부여(`TileCD`/`MineableCD`/`DiggableCD` 제외 → 자원 복제 방지). `NoBreakZoneMod`(IMod)는 로드 로그용. 라이브 `...\Mods\NoBreakZone\`에 설치 완료. **인게임에서 한 번도 안 돌려봄.**

### 다음 세션이 이어서 할 일 — 인게임 검증 (사람)

1. **① 자원 복제(최우선)** — 드릴로 광석 정상 고갈? 곡괭이로 벽/광석 정상 채굴? 작물 수확 정상?
2. **② 보호** — 상자·작업대 곡괭이로 때려도 안 부서짐? 폭탄에 살아남음?
3. AI가 `Player.log`에서 `[NoBreakZone] loaded` + 런타임 에러 확인.
4. 통과하면 검증 완료로 갱신 → 5·6단계(토글/좌표/파일런)로. 실패하면 원인 분석 후 코드 수정.

> 알려진 한계: `DestroyNearbyEntitiesOnDeath`(특정 폭탄류)는 보호 우회 가능. 테스트에서 관찰.

개발 루프(확립됨):
```
코드 수정 → 에디터 닫기 → Editor/build.ps1 → ...\Mods\NoBreakZone\ → 사람이 게임 재로드 → Player.log 확인
```

디스크에서 확인한 사실:

- Unity + Core Keeper Mod SDK가 설치·오픈된 상태다. 프로젝트가 한 번 이상 열렸다 (`Library/` 빌드됨, `.csproj` 생성됨).
- SDK 예제 모드가 `Assets/Examples`에 존재한다 (빌드 테스트에 쓸 수 있음).
- 모드 껍데기가 만들어져 있다:
  - `NoBreakZone.asmdef` — ECS/Burst/NetCode + PugMod.SDK 참조 포함
  - `Data/NoBreakZone.asset` — 모드 정의 에셋 (`enableOverloading: 0`)
- 자체 git repo 초기화됨 (커밋: Init, onboarding).
- **실제 게임 로직 코드(.cs)는 0개.**

아직 확인 못 한 것 (다음 세션에서 검증):

- Unity Editor에 **Linux Build Support (Mono)** 모듈이 설치됐는지
- `-disable-assembly-updater` 커맨드라인 인자가 설정됐는지
- SDK 예제 모드를 실제로 빌드해 게임에서 작동시켜 봤는지

작업자는 유니티를 다뤄본 적이 없다. GUI 조작이 필요한 단계는 구체적으로 안내한다.

---

## 다음에 할 일

### 1단계 — 개발 환경 구축 (대부분 완료, 검증만 남음)

- [x] Unity Hub / Unity Editor 설치 (프로젝트가 열림 — 상세 버전 미검증)
- [ ] Unity Editor에 **Linux Build Support (Mono) 모듈** 설치 여부 확인 *(미확인)*
- [ ] 프로젝트에 `-disable-assembly-updater` 커맨드라인 인자 설정 여부 확인 *(미확인)*
- [x] Core Keeper Mod SDK 클론 (프로젝트 파일 존재)
- [x] Unity Hub에 프로젝트 추가하고 열리는지 확인 (`Library/`·`.csproj` 생성됨)
- [ ] SDK에 포함된 예제 모드를 빌드해 게임에서 작동 확인 *(미확인 — 2단계와 함께 진행)*

**공식 문서를 먼저 읽는다** — `https://modding.corekeepergame.com` 의 환경 설정 항목. SDK 버전이 올라갔을 수 있으므로 문서 쪽을 우선한다.

### 2단계 — 개발 루프 확립 (조사 완료, 스크립트 작성 대기)

조사 결과는 `research.md`에 기록됨. 요약:

- [x] 커맨드라인 빌드 가능 여부 — 가능하나 **전용 메서드 없음**. `ModBuilder.BuildMod`를 감싸는 에디터 래퍼 필요. 배치모드는 **에디터를 닫아야** 락 충돌이 없음
- [~] 게임 재시작 없이 모드 재적용 가능 여부 — 코드상 게임 시작 시 로드로 추정. **인게임 실검증 필요**
- [x] 모드 폴더 경로 — `D:\SteamLibrary\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods\NoBreakZone\`
- [x] 로그 파일 경로 — `C:\Users\LeeShinYeoung\AppData\LocalLow\Pugstorm\Core Keeper\Player.log`
- [x] `build` 스크립트 작성 — **CLI 배치 방식. 첫 빌드 성공 (2026-07-27).**
  - `Assets/NoBreakZone/Editor/CliBuild.cs` — `ModBuilder.BuildMod` 배치모드 래퍼
  - `Assets/NoBreakZone/Editor/NoBreakZone.Editor.asmdef` — 전용 에디터 어셈블리
  - `Assets/NoBreakZone/Editor/build.ps1` — 에디터 락 확인 → Unity 배치 호출 → 종료코드 보고
  - 산출물: `...\Mods\NoBreakZone\`에 Windows+Linux 번들 + `ModManifest.json`. Linux 모듈·배치 파이프라인 확인됨 (`research.md` 6장)

**개발 루프 (확정):**
```
코드 수정 → (에디터 닫기) → build.ps1 실행 → ...\Mods\NoBreakZone\ 갱신
         → 사람이 게임 실행/재로드 → 확인 → Player.log 확인
```
남은 인게임 검증: 모드가 실제로 로드/활성화되는지, 재적용에 전체 재시작이 필요한지 (`research.md` 7장).

**이 단계가 끝나야 이후 작업 속도가 정해진다.** 서두르지 말고 확실히 한다.

### 3단계 — 게임 코드 조사

- [ ] AssetRipper 등으로 게임 에셋·어셈블리 추출
- [ ] 설치물 파괴가 어떤 구조인지 확인 (채굴 등급 / 체력 / 기타)
- [ ] 해당 시스템이 Burst 컴파일 대상인지 확인
- [ ] 기존 설치물 스프라이트 규격과 팔레트 확인
- [ ] 기존 범위 표시(아이템 수집기 등) 구현 방식 확인
- [ ] 결과를 `research.md`에 기록하고 기획서의 `확인 필요` 항목 갱신

### 4단계 — 피해 차단 검증

기획서 13장 1단계에 해당한다. 하드코딩으로 모든 설치물이 파괴되지 않게 만들고 게임에서 확인한다.

**여기서 막히면 설계를 다시 봐야 한다.** 이후 작업의 전제가 된다.

---

## 제공된 것

모두 `Assets/NoBreakZone/Editor/Docs/` 아래에 있다. (Editor 폴더라 빌드된 모드에는 포함되지 않는다 — 2단계에서 번들 오염 발견 후 이동)

| 항목 | 위치 |
| --- | --- |
| 기획서 | `Editor/Docs/design.md` |
| 워크스페이스 정의서 | `Editor/Docs/workflow.md` |
| 스프라이트 초안 | `Editor/Docs/art/*.png` (lens, pylon_on/off, remote, workbench, preview) |
| 스프라이트 생성 스크립트 | `Editor/Docs/art/sprites.py` |
| 오브젝트 DB 참조 | `Editor/GameData/object_flags.csv` |

스프라이트는 **규격과 팔레트가 확인되기 전의 초안**이다. 3단계에서 확인된 것: 1타일 = 16px,
팔레트 제약 없음(컬러 직접 사용 가능), 32px 초안은 월드에서 2×2 타일로 보인다(위 참조).
`pylon_off.png`가 `genassets.py`를 통해 `Textures/NoBreakZonePylon.png`로 복사돼 실제로 쓰인다 —
`art/` 쪽이 원본이고 `Textures/` 쪽은 생성물이므로 **고칠 때는 `art/`를 고친다.**

---

## 아직 없는 것

- `Editor/Docs/failures.md` — 필요해지면 만든다
- deploy-prep 스킬 — 배포 절차(`workflow.md` 6장)가 확인된 뒤에 만든다

---

## 기록 방법

작업이 끝날 때마다 이 문서를 갱신한다. 체크박스를 채우고, 알게 된 사실은 `research.md`로, 실패한 시도는 `failures.md`로 보낸다.

**세션을 새로 시작한 사람이 이 문서만 읽고 이어갈 수 있어야 한다.**
