# 조사결과

게임 코드·SDK·환경에서 확인한 사실. 근거를 남긴다. **코드/파일을 읽어 확인한 것**과 **아직 실행으로 검증하지 못한 것**을 구분해서 적는다.

최초 작성: 2026-07-27 (2단계 개발 루프 조사)

---

## 1. 개발 환경 상태 (2026-07-27 기준)

| 항목 | 상태 | 근거 |
| --- | --- | --- |
| Unity Editor | ✅ `6000.0.59f2` 설치됨 (`.58f2`도 있음) | `ProjectSettings/ProjectVersion.txt`, `C:/Program Files/Unity/Hub/Editor/6000.0.59f2/Editor/Unity.exe` |
| 게임 어셈블리 임포트 | ✅ DLL 205개 | `Assets/Plugins/CoreKeeper/*.dll` |
| SDK 어셈블리 | ✅ DLL 8개 + Harmony | `Assets/Plugins/CoreKeeperModSDK/` |
| 게임 설치 경로 | ✅ `D:\SteamLibrary\steamapps\common\Core Keeper` | Steam `libraryfolders.vdf`, 디렉터리에 `CoreKeeper.exe` 존재 |
| 게임 경로 EditorPrefs 등록 | ✅ 등록됨 | 레지스트리 `HKCU\...\Unity Editor 5.x\PugMod/SDKWindow/GamePath` → 디코딩 시 위 경로 |
| 모드 스캐폴드 | ✅ 표준 생성됨 | `Assets/NoBreakZone.asset`(ModBuilderSettings) + `Assets/NoBreakZone/`(asmdef, Data) |
| 실제 모드 코드(.cs) | ❌ 아직 0개 | — |

**결론: 코드를 작성하면 곧바로 컴파일·빌드할 수 있는 상태다.** 1단계에서 미확인이던 항목 중 게임 어셈블리 임포트와 경로 설정은 이미 되어 있다.

미확인으로 남은 것: **Linux Build Support (Mono) 모듈** 설치 여부, `-disable-assembly-updater` 인자. 둘 다 첫 실빌드에서 드러난다.

---

## 2. 빌드 메커니즘

핵심 함수는 `ModBuilder.BuildMod(settings, exportPath, callback, installInSubDirectory=true)` — **동기 실행**(콜백을 인라인으로 호출, 비동기 아님).
`Packages/dev.pugstorm.mod/SDK/Editor/ModBuilder.cs`

빌드 산출물 (`exportPath/<modName>/` 아래):
- `Scripts/` — 모드의 `.cs` 원본을 복사 (게임이 런타임에 Roslyn으로 컴파일). **Editor·CodeGen 폴더는 제외**된다 (`IsInEditorFolder`).
- `Libraries/` — 포함된 `.dll`
- `Bundles/` — `AssetBundle`(Windows + Linux). `buildLinux=1`이면 리눅스도 빌드.
- `Conf/`(json), `Localization/`(csv) — 있으면 그대로 복사.
- `modmanifest.json` — 매니페스트.

`Assets/NoBreakZone.asset`(ModBuilderSettings)의 현재 값: `modPath=Assets\NoBreakZone`, `buildBundles=1`, `buildLinux=1`, `metadata.name=NoBreakZone`, `metadata.guid=978e3f5e5edb4f1f919c3740cca92b6b`.

### 빌드를 호출하는 GUI 경로 (사람)

`PugMod ▸ Open Mod SDK Window ▸ Create Mod 탭`:
- **Build Mod** 버튼 → 임시폴더(`%TEMP%/BuiltMods`)에 빌드
- **Install Mod** 버튼 → 임시빌드를 게임 Mods 폴더로 복사
- **Local Export** 버튼 → 게임 Mods 폴더로 **직접** 빌드 (한 번에)

세 경로 모두 게임 경로 EditorPrefs를 사용하므로, "Find game files"에서 경로를 잡아둔 지금은 바로 동작한다. (`CreateMod.cs`)

### 커맨드라인(CLI) 빌드 — **전용 메서드 없음, 래퍼 필요**

`ModBuilder.BuildMod`은 `public static`이라 `Unity.exe -batchmode -executeMethod`로 호출 가능하지만, 인자로 `ModBuilderSettings`와 `exportPath`를 받으므로 **직접 호출할 수 없다.** 에디터 래퍼 메서드를 새로 작성해야 한다:

```
Unity.exe -batchmode -quit \
  -projectPath "C:\Unity\CoreKeeper" \
  -executeMethod <래퍼클래스>.<메서드> \
  -logFile <빌드로그경로>
```

래퍼가 할 일: `Assets/NoBreakZone.asset` 로드 → 게임 Mods 경로 계산 → `BuildMod(settings, modsPath, cb, installInSubDirectory:false)` 호출 → 성공/실패로 종료코드 반환.

**⚠️ 제약: 에디터 락.** 배치모드는 같은 `projectPath`를 다른 Unity 인스턴스(에디터)가 점유 중이면 실패한다 (`Temp/UnityLockfile`). 즉 CLI 빌드를 쓰려면 **작업 중 에디터를 닫아둬야** 한다. 조사 시점(2026-07-27)에는 에디터가 열려 있었다.

---

## 3. 모드 폴더 경로

로컬(비-mod.io) 모드는 게임의 StreamingAssets 아래에 놓으면 로드된다. (`CreateMod.cs`)

```
D:\SteamLibrary\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods\<modName>\
```
- 데디케이티드 서버면 `CoreKeeperServer_Data\StreamingAssets\Mods\`.
- 현재 이 `Mods\` 폴더는 **비어 있다** (로컬 모드 아직 없음).

NoBreakZone의 최종 설치 위치:
`D:\SteamLibrary\steamapps\common\Core Keeper\CoreKeeper_Data\StreamingAssets\Mods\NoBreakZone\`

---

## 4. 로그 경로

게임(클라이언트) 로그:
```
C:\Users\LeeShinYeoung\AppData\LocalLow\Pugstorm\Core Keeper\Player.log
                                                              Player-prev.log  (직전 실행)
```
`Player.log`에서 모드 로딩·에러를 확인한다. 조사 시점 로그에 mod.io 모드 `Placement Plus` 로드, `got all mod infos from server`, 종료 시 `Exit blocked by ModManager` 등이 보였다 — 로그가 모드 활동을 담는다는 것 확인.

에디터/임포트 로그: 프로젝트 `Logs/`(AssetImportWorker*.log). CLI 빌드 시에는 `-logFile`로 별도 지정.

---

## 5. 모드 재적용 방식 (핫 리로드) — **실행 검증 필요**

코드 조사 기준 추정: 로컬 모드는 `StreamingAssets\Mods\`에서 **게임 시작 시 로드**되고, 스크립트는 로드 시 컴파일된다. 런타임 C# 핫 리로드 경로는 확인되지 않았다.

따라서 현재 예상 루프:
```
코드 수정 → (에디터 닫고) CLI 빌드 → Mods\NoBreakZone\ 갱신 → 게임 재시작(또는 타이틀 복귀/월드 재로드) → 확인 → Player.log 확인
```

**사람이 게임에서 확인해줘야 할 것:**
- 로컬 모드가 인게임 모드 메뉴에서 자동 인식/활성화되는가, 수동 활성화가 필요한가
- 전체 재시작 없이 타이틀 복귀·월드 재로드만으로 재적용되는가 (루프 속도에 직결)

---

## 6. 첫 빌드 결과 (2026-07-27)

`build.ps1` 첫 실행 — **성공.** 로그: `Successfully built mod at ...\Mods\NoBreakZone`, `[NBZ] BUILD OK`.

산출물 `...\Mods\NoBreakZone\`:
- `Bundles\NoBreakZone_Windows.assetbundle` (+ `.manifest`)
- `Bundles\NoBreakZone_Linux.assetbundle` (+ `.manifest`) ← **Linux Build Support 모듈 설치 확인됨**
- `ModManifest.json` (guid `978e3f5e...`, files 4개, dependencies 없음)

이로써 미확인 3개 해소:
- [x] Linux Build Support (Mono) 모듈 — Linux 번들 정상 생성
- [x] 배치모드 빌드(AssetBundle) 성공
- [x] CLI 래퍼(`CliBuild.BuildToGame`)가 배치모드에서 정상 실행

### 주의사항 (빌드 관련)

- **`build.ps1` 실행 전 에디터를 닫아야 한다.** 스크립트가 `Temp/UnityLockfile`을 확인해 열려 있으면 종료코드 2로 거절한다.
- Unity.exe는 GUI 서브시스템 앱이라 PowerShell `&` 호출이 대기하지 않는다. `build.ps1`은 `Start-Process -Wait -PassThru`로 종료코드를 받도록 수정됨.
- 컴파일 중 로그에 `MonoMod.Utils`/`0Harmony` "Failed to resolve System.Reflection.Emit.Label / Mono.Cecil" 경고가 다량 뜨지만 **빌드를 막지 않는 노이즈**다. Harmony DLL을 에디터 Mono에서 스캔할 때 발생.
- 콜드 스타트 빌드 소요: 대략 수 분 (코드 0줄 기준).
- **⚠️ 에디터 빌드 컴파일 ≠ 게임 런타임 컴파일.** `build.ps1`이 성공해도(에디터가 asmdef 컴파일 OK) **게임은 로드 시 Scripts/를 자체 Roslyn으로 다시 컴파일 + 보안검사(safetyCheck)** 한다. 여기서 실패하면 게임에 "다음 모드를 로딩하지 못했습니다: NoBreakZone(컴파일링 실패)" 팝업. **진짜 컴파일 검증은 게임 로드 시점**이고, 에러는 `Player.log`의 `mod NoBreakZone load error: CompileFailed` + `Referenced in method body ... in source file ... at line ...`로 찍힌다.
- **⚠️ 모드 코드에서 `System.Reflection` 금지.** 보안검사가 모드 IL의 리플렉션 호출을 거부한다. 예: `Type.Name`(=`MemberInfo.get_Name`), `GetType()` 직접 호출 → 거부. 컴포넌트 타입명이 필요하면 `ComponentType.ToString()`을 쓴다(리플렉션이 있더라도 신뢰된 게임 어셈블리 내부에서 일어나 통과). 참고로 `enum.ToString()`(문자열 보간 `$"{id}"` 포함)은 통과한다 — 리플렉션이 프레임워크 내부라 모드 IL에 안 남기 때문.
- **모드 폴더(`modPath`) 안의 모든 자산이 번들에 실린다.** `ModBuilder`는 `.cs`(→Scripts), `.dll`(→Libraries), `Conf/*.json`, `Localization/*.csv`, `Editor`/`CodeGen` 폴더를 제외한 **나머지 전부**를 AssetBundle로 쓸어담는다. 첫 빌드에서 `Editor/Docs/`의 기획 문서·초안 PNG·`sprites.py`가 번들에 포함되고 게임 로그에 `couldn't load ... sprites.py` 경고가 떴다. → **개발 문서·초안은 `Editor/` 하위(또는 modPath 밖)에 둔다.** 배포 대상 아닌 것을 `Assets/NoBreakZone/` 루트에 두지 말 것. (`Docs/`을 `Editor/Docs/`으로 이동해 해결)

## 8. 게임 코드 조사 (3단계) — 파괴 파이프라인

디컴파일러 `ilspycmd`(스크래치패드 `tools/`에 설치, NuGet)로 게임 어셈블리를 역컴파일해 확인. **모든 게임로직은 `Pug.Other.dll`에 있다** (`Assembly-CSharp.dll`은 렌더링만). 컴포넌트는 `Pug.ECS.Components.dll`, `ObjectType`/`TileCD`는 `Pug.Base.dll`, 드릴은 `Pug.Automation.dll`.

### 피해 → 파괴 흐름 (모든 피해원이 하나로 수렴)

전투·폭발·조건·타일채굴·드릴 **모두** `HealthChange`를 공용 싱글턴 버퍼 `HealthChangeBuffer`에 넣는다. 이후 순서대로:

1. `TileDamageSystem` — 타일/채굴 피해를 `HealthChange`로 변환. 벽·광석은 `HealthCD`+`TileCD`를 가진 **엔티티**다.
2. `UpdateHealthFromBufferSystem` — `HealthChange` 적용, `health`를 `[0,max]`로 클램프. **파괴는 안 함.**
3. **`SetEntitiesDestroyedSystem`** ← 핵심 관문. `health<=0`이고 `DontDestroyOnZeroHealthCD{disabled=false}`가 **없으면** `EntityDestroyedCD`를 enable. 있으면 `return`(파괴 안 함).
4. `DestroyEntitiesSystem`(서버 전용) — 타이머 만료 시 `ecb.DestroyEntity`.

전부 `[BurstCompile]` job → **본문 Harmony 패치 불가.** 반드시 컴포넌트(데이터) 레벨로 개입한다.

### 핵심 결론 (기획서 §9 "표식" 방식과 일치)

**`DontDestroyOnZeroHealthCD{disabled=false}` 부여 = 파괴 차단.** 체력은 0까지 깎여도 엔티티는 살아남는다. 모든 피해원이 같은 관문을 지나므로 이 한 컴포넌트로 전부 막힌다.

- `DontDestroyOnZeroHealthCD { bool disabled; }` (GhostComponent, 네트워크 동기). `disabled=false`가 보호.
- **`ImmuneToDamageCD`는 전투 경로에서만 체크됨** → 채굴/드릴/폭발 피해는 못 막음. 보호용으로 부적합. (`ImmuneToDamageState { Vulnerable=0, Immune=1 }`, `Pug.Base.dll`)

### ⚠️ 자원 복제 위험 — 반드시 제외할 것

광석·벽·작물도 **같은 `SetEntitiesDestroyedSystem` 관문**으로 파괴된다. `HealthCD` 가진 것에 무차별 보호를 붙이면 드릴이 광석을 0으로 깎아도 안 사라져 **광석 무한 복제** (기획서 §6 "절대 발생 금지"). → 보호 대상에서 `TileCD`/`MineableCD`/`DiggableCD` 제외.

### 설치물 vs 지형 vs 광석 vs 작물 판별

전용 "player-placed" 플래그는 **없다.** 조합으로 판별:

| 구분 | 판별 컴포넌트 |
| --- | --- |
| 설치물(상자·작업대 등) | `ObjectTypeCD.Value == ObjectType.PlaceablePrefab`(=800) + `HealthCD`, 피해가능 시 `DamageableObjectCD`/`DestructibleObjectCD`. `TileCD`/`MineableCD`/`DiggableCD` **없음** |
| 지형·벽 | `TileCD{tileset,tileType}` + `TileDamageTagCD`, 벽은 `MineableCD`도 |
| 광석 | `TileCD.tileType==ore` 또는 `MineableCD`+`RequiresDrillCD` |
| 작물 | `PlantCD`+`GrowingCD`, 일부 `DiggableCD` |

컴포넌트 정의: `HealthCD{int health;int maxHealth}`, `ObjectTypeCD{ObjectType Value}`, `ObjectDataCD{ObjectID objectID;int amount;int variation;int variationUpdateCount}`, `DamageableObjectCD`/`DestructibleObjectCD`/`DiggableCD`=빈 태그, `MineableCD{bool playFailedEffectOnZeroDamage}`.

### 커스텀 서버 시스템 작성법

`PugSimulationSystemBase : SystemBase` 상속. `[WorldSystemFilter(ServerSimulation)]` + `[UpdateInGroup(typeof(SimulationSystemGroup))]`. 제공 헬퍼: `CreateCommandBuffer()`(BeginSimulationECB), `isServer`, `GetServerTick()`, `CreateTileAccessor()` 등. `OnCreate`에서 `base.OnCreate()` 후 쿼리 구성, `OnUpdate` 끝에 `base.OnUpdate()` 호출. 기본적으로 `InitialLoadingDoneCD` 준비될 때까지 대기.

> **주의:** `Entities.ForEach`의 `WithNone<T1..T4>`는 arity 초과로 컴파일 실패(`CS1061`). 다중 제외는 **명시적 `GetEntityQuery(EntityQueryDesc{All,None})` + 수동 순회**가 안전. (실패기록)

> **보호 우회 경로(한계):** `DestroyNearbyEntitiesOnDeathSystem`이 `destroyEntitiesWithDontDestroyOnZeroHealthCD=true`인 죽음-효과(특정 폭탄류)일 때 `DontDestroyOnZeroHealthCD` 보호를 무시하고 직접 파괴. 좁은 경로지만 100% 절대적이지는 않음. 4단계 테스트에서 관찰 필요.

## 9. 파괴 차단 실제 구현 — 클라이언트 예측 함정 (4단계 검증 완료)

**증상:** 서버에서만 `DontDestroyOnZeroHealthCD`/`ImmuneToDamageCD`를 붙였더니, 상자가 안 부서지긴 하는데(서버는 살림) **투명해지고 팬텀 아이템이 무한 생성**됨. 23시간 삽질의 정체.

**원인 (디컴파일로 규명):**
- 곡괭이/공격/폭발 피해는 `PlayerAttackSystem`을 통해 **클라이언트+서버 양쪽에서 예측 실행**된다 (`ServerSimulation|ClientSimulation`, `PredictedSimulationSystemGroup`).
- 그 경로(`PlayerController.DealDamageToObject`)는 **`IndestructibleCD`만** 본다. `ImmuneToDamageCD`는 이 오브젝트-채굴 경로에서 **안 봄**(적/서버 전용 `AttackSystem`에서만). → 내 `ImmuneToDamageCD`는 무의미했음.
- **NetCode는 고스트의 동기 컴포넌트 집합을 굽는 시점에 고정**한다(`GhostCollectionSystem`). 런타임에 서버에서 `AddComponent`한 건 클라로 **동기화 안 됨**. → 클라는 보호를 모른 채 계속 파괴를 예측 → 스냅샷으로 롤백 → 매 곡괭이질마다 깜빡임.

**해결 (검증됨):** 게임 자체 플래그 **`IndestructibleCD`**(enableable, `[GhostEnabledBit]`)를 켠다. 예측 경로가 이게 켜져 있으면 **피해를 아예 안 넣어** 체력이 안 깎이고 파괴도 없다. 동기화가 안 되므로 **시스템을 `ServerSimulation|ClientSimulation` 양쪽 월드에서 돌려** 각 월드가 자기 로컬 엔티티에 켠다. 구조 변경(AddComponent)은 예측 롤백에 **안 지워진다**(`GhostUpdateSystem`은 데이터/enable비트 memcpy만, 컴포넌트 추가/제거 안 함) — 이게 핵심.
- 백스톱으로 `DontDestroyOnZeroHealthCD`도 양쪽에서 부여(예외 피해 경로 대비).
- 판별자는 `object_flags.csv` 전체로 검증: `PlaceablePrefab`+`HealthCD` 중 `DestructibleObjectCD`/`DropsLootFromLootTableCD`/`DropsLootWhenDamagedCD` 가진 자원(광석·항아리·벽)은 제외 → 채굴/자원복제 안전.

**교훈:** 네트워크 게임 모드에서 "런타임에 서버에서만 컴포넌트 붙이기"는 클라 예측을 못 막는다. (1) 클라도 보는 경로의 플래그를 쓰고, (2) 동기화가 안 되면 양쪽 월드에서 각자 적용한다.

## 10. 조사 재료의 위치 — **윈도우 없이도 전부 읽힌다** (2026-08-04)

가장 큰 발견. **게임 어셈블리를 맥으로 옮길 필요도, 디컴파일러를 설치할 필요도 없다.**
전부 공개 저장소에 있고 최신이다. 스크래치패드에 클론해서 grep 한다.

| 저장소 | 내용 | 쓰임 |
| --- | --- | --- |
| `Adrriiannn/ck-db` | 게임 전체 디컴파일 소스. `.cs` **4,476개**를 어셈블리별로 정리 | 게임이 **어떻게 동작하는지** |
| `Pugstorm/CoreKeeperModDocs` | 공식 모딩 문서 + `code-examples/` | SDK를 **어떻게 쓰는지** |
| `Adrriiannn/ck-mods` | 같은 저자의 모드 3개(ConveyorTunnel·ChunkLoader·SmartSplitter). SDK 프로젝트 통째 사본 | **실제로 어떻게 짜는지**. 커스텀 설치물 완본 |
| `CoreKeeperMods/CoreLib` | 모딩 라이브러리. **`Packages/` 밑에 SDK 패키지 원본**(`dev.pugstorm.mod`·`scriptabledata`·`sprite`)이 들어 있다 | 디컴파일이 아닌 원본이 필요할 때 |
| SDK `Assets/Examples.zip` | 공식 예제 모드 10개 (Item·Workbench·Enemy·Rpc 등) | 최소 템플릿 |

`ck-mods`는 sparse를 `Assets Docs`로 넓히면 모드가 **6개**다 — ConveyorTunnel·ChunkLoader·
SmartSplitter에 더해 `Nullforge`·`ExpandNullforge`(.cs 576개)·`MPTest`. 포탈 같은 복잡한
커스텀 오브젝트 예시는 여기 있다.

**신뢰도 검증:** 8장이 디컴파일로 알아낸 심볼(`SetEntitiesDestroyedSystem`·`DontDestroyOnZeroHealthCD`·
`IndestructibleCD`·`HealthChangeBuffer`·`TileDamageSystem`·`PlayerController.DealDamageToObject`)이
`ck-db`에 전부 같은 이름·같은 위치로 존재하고 본문(job 구조체·쿼리·lookup)도 살아 있다.

**주의:** `ck-db`는 2026-05-16 기준 비공식 3자 저장소다. 게임 업데이트 시 어긋난다.
**최종 근거는 언제나 윈도우 빌드 통과 여부다.**

⚠️ 저장소 안에 클론하지 말 것. `.dll`·`.cs`가 modPath 루트에 있으면 번들에 실린다
(`Editor/` 아래는 제외됨 — `ModBuilder.cs:312,403,442`).

### 세션마다 다시 받는 법 (스크래치패드에서)

```sh
git clone --depth 1 --filter=blob:none https://github.com/Adrriiannn/ck-db.git ck-db
git clone --depth 1 --filter=blob:none --sparse https://github.com/Adrriiannn/ck-mods.git ck-mods
(cd ck-mods && git sparse-checkout set Assets Docs)   # 모드 6개 전부
git clone --depth 1 --filter=blob:none https://github.com/CoreKeeperMods/CoreLib.git CoreLib
git clone --depth 1 --filter=blob:none --sparse https://github.com/Pugstorm/CoreKeeperModDocs.git
(cd CoreKeeperModDocs && git sparse-checkout set modding-documentation code-examples)
curl -sLO https://raw.githubusercontent.com/Pugstorm/CoreKeeperModSDK/main/Assets/Examples.zip && unzip -q Examples.zip
```

가장 많이 열어보게 되는 파일들:

| 무엇을 볼 때 | 어디 |
| --- | --- |
| 설치물 프리팹 전체 구성 | `Examples/WorkbenchExample/Workbench/MyNewWorkbenchLogic.prefab` |
| 그래픽 프리팹 최소 구성 | `ck-mods/…/ConveyorTunnelMod/Prefabs/ConveyorTunnelVisual.prefab` |
| 스프라이트 임포트 설정 | `ck-mods/…/ConveyorTunnelMod/Assets/ConveyorTunnelIcon.png.meta` |
| authoring 컴포넌트 필드 | `ck-db/Pug.ECS.Authoring/*.cs` |

### 여기서 확정된 사실

| 항목 | 결론 | 근거 |
| --- | --- | --- |
| 오브젝트 → 타일 좌표 | `LocalTransform.Position.RoundToInt2()`. **XZ 평면** (`(int2)math.round(float2(x.x, x.z))`) | `Pug.UnityExtensions/ExtensionMethods.cs:551` |
| 다중 타일 점유 계산 | `tile += prefabCornerOffset` 후 `[tile, tile+prefabTileSize)` 순회. `DirectionCD` 있으면 `GetPrefabOffsetAndTileSize`로 회전 반영 | `Pug.Other/DetectRoomSystem.cs:167-192` |
| 오브젝트 크기 | `ObjectInfo.prefabTileSize`(Vector2Int), `prefabCornerOffset` | `Pug.Base/ObjectInfo.cs:87,90` |
| ObjectID 등록 | `API.Authoring.GetObjectID(name)` — **단순 딕셔너리 조회. 이름에 제약 없음** (점 OK) | `Pug.Other/PugMod/ModAPIAuthoring.cs:28` |
| 토글 + 세이브 | `ObjectAuthoring.variation` / `variationIsDynamic` / `variationToToggleTo`. 월드 세이브가 자동 보관 | `Pug.ECS.Authoring/ObjectAuthoring.cs:132-138` |
| 상호작용(E) | `InteractableObject`(그래픽 프리팹에 붙음), `InteractWithEnvironmentSystem` | `Pug.Other/` |
| 안정 API | `API.Server.World` · `API.Effects.PlayPuff` · `API.Audio.PlaySfx` · **`API.ConfigFilesystem`**(모드 전용 샌드박스 파일 IO) | 공식 문서, `ConveyorTunnelPersistence.cs` |
| 레시피 주입 | `SingleAuthoringComponentConverter<CraftingAuthoring>` 상속 | `ConveyorTunnelRecipeInjectionConverter.cs` |
| 효과음 ID 목록 | `ck-mods/Docs/CoreKeeper-SfxID-list.txt` | — |

### ⚠️ 스프라이트 규격 — 우리 초안이 2배 크다

**1타일 = 16px.** 레퍼런스 모드 실측: 인벤토리 아이콘 16×16, 손에 든 것 10×10,
4타일 벨트 64×16. SDK 예제 작업대 16×18.

`Editor/Docs/art/*.png` 초안은 파일런 32×32, 작업대 64×32 — **정확히 2배**다. 재생성 필요.
컬러는 직접 칠하지 않고 **그레이스케일 + GradientMapDataBlock** 방식을 쓴다
(`ck-mods/…/Workbench/Grayscale/`, `Data/GradientMapDataBlock/`).

### 5단계 위험 — 재사용할 범위 표시가 없다

기획서 §7은 "게임에 이미 있는 범위 표시 방식을 그대로 따른다(아이템 수집기 등)"고 지시하는데,
**게임 소스에서 재사용 가능한 범위 표시 컴포넌트를 찾지 못했다.** 레퍼런스 모드도 `SpriteRenderer`를
직접 풀링해서 마커를 깐다(`ConveyorTunnelPlacementGuideController.cs`). 기획서 §7의 대안 조항으로
가야 한다 — **5단계 착수 전 승인 필요.**

## 11. 유니티 없이 프리팹을 작성하는 법 (2026-08-04)

프리팹·ScriptableObject는 전부 평범한 YAML이다. **유니티를 열지 않고 손으로 쓸 수 있다.**
막는 건 `m_Script: {fileID: N, guid: G}` 한 줄뿐인데, 그게 전부 계산·조회 가능하다.

### fileID 는 클래스 이름에서 계산된다

```
fileID = int32_little_endian( MD4(b"s\0\0\0" + 네임스페이스 + 클래스명)[:4] )
```

구현은 `Editor/preflight.py`의 `md4()` / `script_file_id()`. 표는
`Editor/GameData/script_fileids.csv` (게임 클래스 3,235개, 충돌 0건).

**검증:** 레퍼런스 프리팹 20개에서 뽑은 `m_Script` 참조 295건 중 게임 authoring 어셈블리
**184건이 100% 일치.**

### ⚠️ guid 는 프리팹에서 베끼면 안 된다 — 유니티에게 묻는다 (2026-08-06 정정)

**이 장이 원래 적어둔 guid는 전부 틀렸다.** 아래 표가 그 틀린 값이고, 기록으로 남긴다.

| 이 장이 적었던 guid | 실제로 이 설치본의 값 | 어셈블리 |
| --- | --- | --- |
| `3392f4c23e1d8662d749dabb2361ee02` | **`9a1db145e9314404d8da91bdc1557235`** | `Pug.ECS.Authoring` |
| `292700ef68995bdb2163e35989fc7eb0` | **`b3f0ec2c519d21b49bf19f2d86bba8d7`** | `PugSprite` |
| `e853a5af7d19630282ad0af7b5dabadc` | **`d00b036db31ed324f99d4111c393f019`** | `ScriptableData` |
| `548e3dd2d27c1e2d0bdf82f0889cb8a7` | **`3519ac58e5ff54941a4a69512016923c`** | `Pug.Other` |
| `6f4e9f12d8be4d048a7b574866c31a4f` + fileID `11500000` | **`3519ac58…`** + fileID **`1240517312`** | `EntityMonoBehaviour` |

**대가:** 생성 에셋의 모든 `m_Script`가 이 프로젝트에 존재하지 않는 어셈블리를 가리켰다.
유니티가 타입을 못 찾아 **깨진 참조를 번들에 써넣었고, 빌드는 종료 코드 0으로 성공을 보고했다.**
게임은 프리팹도 아이템 정의도 하나도 못 읽었다 — 작업대에 레시피가 안 뜬 1차 원인이다.
`Assets/`·`Packages/`·`Library/PackageCache`의 `.meta` 12,167개를 전수 조사해 확인했다.

**틀린 이유:** 레퍼런스 모드와 `Assets/Examples`가 그 값을 쓴다(예제 프리팹에 116회 등장).
하지만 **예제는 아무도 빌드해본 적이 없어서** 그게 이 설치본에서 유효한지 검증된 적이 없다.
"프리팹에서 관측한 guid를 쓸 것"이라던 옛 지침이 정확히 이 사고를 만들었다.
`MetaFiles.zip`과 안 겹친다는 것은 **프리팹 쪽이 낡았다는 신호였는데 반대로 읽었다.**

**올바른 방법:** `Editor/DumpScriptGuids.cs`를 배치모드로 돌려
`Editor/GameData/script_guids.csv`를 만든다. 유니티가 `AssetDatabase`로 직접 답한
`(fullName, fileID, guid)` 8,186행이다. `genassets.py`의 `game_script()`가 여기서 조회하므로
**fileID와 guid가 같은 행에서 나와 서로 어긋날 수 없다.** 게임·SDK 업데이트 후 재생성한다.

`preflight.py`가 이제 모든 `(fileID, guid)` 쌍이 실재하는지 검사하고(프리팹 + `.asset`),
`CliBuild.cs`가 `is missing` 경고를 빌드 실패로 처리한다. 같은 사고가 다시 조용히 지나갈 수 없다.

### fileID 계산은 맞았다

위 사고에도 **fileID는 전부 정확했다.** 유니티 덤프와 대조해 `ObjectAuthoring`(318086258)·
`SpriteObject`(1908045241)·`TextDataBlock`(2108018792) 등이 모두 일치했다.
MD4 유도식과 `script_fileids.csv`는 그대로 유효하다. 틀린 것은 **어느 어셈블리 소속이냐**뿐이었다.

### 그 밖의 고정값

| 값 | 의미 |
| --- | --- |
| `fileID: 21300000` | 단일 스프라이트 텍스처(`spriteMode: 1`)의 Sprite 서브에셋. 우리 PNG를 참조할 때 이걸 쓴다 |
| `spriteID: 5e97eb03825dee720800000000000000` | 텍스처 `.meta` 고정값 (레퍼런스 14개 전부 동일) |
| `objectType: 800` | `ObjectType.PlaceablePrefab` |
| 텍스처 임포트 | `textureType: 8`(Sprite) · `filterMode: 0`(Point) · `spritePixelsToUnits: 16` · `alphaIsTransparency: 1` |

### ⚠️ `spritePixelsToUnits`는 월드 크기를 못 바꾼다 (2026-08-05)

**월드에 그려지는 크기는 `텍스처 픽셀 ÷ 16`으로 고정이다.** 32×32 텍스처는 `spritePixelsToUnits`를
무엇으로 두든 **2×2 타일 크기로 렌더된다.**

근거 두 가지:

1. `SpriteObject.PixelsPerUnit`이 코드에 박힌 `static float = 16f`다
   (`ck-db/PugSprite/Pug/Sprite/SpriteObject.cs:1096`). 임포터 값을 읽지 않는다
2. 구조적으로 읽을 수가 없다. `SpriteAsset.m_staticSpriteData.texture`가 가리키는 것은
   **Texture2D 서브에셋(`fileID: 2800000`)**이고, `spritePixelsToUnits`는 그 옆의 **Sprite
   서브에셋(`fileID: 21300000`)**에만 붙는 값이다. 월드 렌더 경로는 Sprite를 거치지 않고
   Texture2D를 아틀라스에 직접 싣는다 (`SpriteAsset.cs`의 `staticAtlasRects`)

`spritePixelsToUnits`가 실제로 영향을 주는 곳은 **인벤토리 아이콘 하나뿐**이다 —
`InventoryItemAuthoring.icon`이 `21300000`, 즉 Sprite 서브에셋을 가리키기 때문이다.

**status.md의 스프라이트 결정 (a)는 이유가 틀렸다.** "`spritePixelsToUnits`를 32로 두면 1타일로
렌더된다"는 성립하지 않는다. 32px 텍스처는 무슨 값을 넣어도 2×2 타일로 보인다.

**결말 (2026-08-06): 인게임에서 확인하고 전부 16px로 다시 그렸다.** 작업대(64×32)가 4×2 타일로
그려지는 것이 눈에 띄어 사용자가 지적했고, 그 자리에서 규격을 확정했다 — 월드에 서는 설치물은
**16×18**, 인벤토리 전용 아이템은 **16×16**, `spritePixelsToUnits`는 전부 **16**.

18은 SDK 예제 작업대(`WorkbenchExample/Workbench/MyNewWorkbench1_down.png`)와 같은 값이다.
그 예제는 `prefabTileSize 1×1`이고, `genassets.py`의 `sprite_offset`이 원래 그 프리팹에서
복사해 온 값이라 **아트 크기를 맞추자 그 오프셋이 비로소 맞는 값이 됐다.**

축소가 아니라 **다시 그렸다.** 32px 초안은 16px 그림을 2배 확대한 것이 **아니다** — 2×2 픽셀
블록이 단색인 비율이 73%뿐이라 나머지 27%는 진짜 서브픽셀 디테일이고, 기계적 축소는 형태를
뭉갠다. `Editor/Docs/art/sprites.py`의 좌표를 새로 잡았다.

### 설치물 프리팹의 구성

로직 프리팹(ECS 엔티티)과 그래픽 프리팹이 쌍을 이루고, `ObjectAuthoring.graphicalPrefab`이
후자를 `{fileID: <그래픽 루트 GameObject의 로컬 id>, guid: <그래픽 프리팹 guid>, type: 3}`로 가리킨다.

로직 프리팹의 컴포넌트 (SDK 작업대 예제 기준):
`ObjectAuthoring` · `InventoryItemAuthoring` · `LocalizationAuthoring` · `MineableAuthoring` ·
`HealthAuthoring` · `PlaceableObjectAuthoring` · `IgnoreVertexOffsetsAuthoring` ·
`StateAuthoring` · `IdleStateAuthoring` · `TookDamageStateAuthoring` · `DeathStateAuthoring` ·
`DamageReductionAuthoring` (+ 작업대는 `CraftingAuthoring`, 회전물은 `RotationAuthoring`)

그래픽 프리팹: 루트에 `EntityMonoBehaviour`, 자식에 SpriteObject, 상호작용하면 `InteractableObject`.

각 컴포넌트의 필드 이름·순서는 `ck-db/Pug.ECS.Authoring/*.cs`에서 읽는다.

### 에셋 사이의 연결 — `m_address` (128비트 자체 선언 ID)

스프라이트와 텍스트는 GUID가 아니라 **`m_address {m_low, m_high}`** 로 연결된다.
`Pug.Base/GuidAsULongs.cs`의 `DataBlockAddress(lowBits, highBits)`가 그 타입이다.

```
SpriteAsset.m_address  ←──  SpriteObject.m_assetRef.m_address     (그래픽 프리팹)
SpriteAssetManifest.spriteAssets[] ──→ SpriteAsset  (이건 guid 참조)
```

### ✅ `m_address` = 그 에셋 파일의 Unity guid (2026-08-05, 해결)

**이전 판의 "미검증 가정"은 틀렸다.** 파일 GUID와 안 맞는다고 적어뒀는데, 바이트 순서를 안 맞춰본
탓이었다.

`m_address`는 128비트 GUID를 **마이크로소프트 배치**(앞 세 필드가 리틀엔디안, `bytes_le`)로
담아 부호 있는 int64 둘로 쪼갠 것이다.

```python
low, high = struct.unpack("<qq", uuid.UUID(hex=에셋_meta_guid).bytes_le)
```

**검증: SDK 공식 예제 18/18 일치.** SpriteAsset · TextDataBlock · GradientMapDataBlock ·
각종 SkinDataBlock 전부.

```
SDK 작업대 SpriteAsset  m_address = (1316430874294400503, -6785571713184725333)
  → bytes_le → c9acb5f7-e6e7-1244-ab86-af757aced4a1
  그 .asset.meta 의 guid = c9acb5f7e6e71244ab86af757aced4a1     ← 동일
```

애초에 `DataBlockAddress`가 GUID 문자열로 만드는 타입이다 —
`Pug.Base/ContentBundleDataBlock.cs`에 `new DataBlockAddress("7507d88e-fd7a-7444-1b18-3816c6fbe382")`,
`Pug.Other/CharacterCustomizationMenu.cs`에 `new DataBlockAddress(Guid.Parse(...))`.

**게임이 요구하는 것은 유일성뿐이다.** ck-mods 계열 28건은 자기 파일 guid와 안 맞는데도 실제로
돌아간다. 파일 guid를 쓰는 것은 **SDK 도구의 관례**이고, 맞춰두면 공짜로 확신이 하나 늘어난다.

`Editor/preflight.py`가 이 대조를 검사한다. 어긋나면 에셋은 멀쩡히 로드되고 **스프라이트만
안 나오므로** 자동 검사가 아니면 잡을 방법이 없다.

### 나머지 고정값

| 값 | 의미 |
| --- | --- |
| `fileID: 2800000` | Texture2D 서브에셋. **`SpriteAsset.m_staticSpriteData.texture`가 이걸 가리킨다** |
| `fileID: 21300000` | Sprite 서브에셋. `InventoryItemAuthoring.icon`이 이걸 가리킨다 |
| `fileID: 11500000` | 클래스가 `.dll`이 아니라 `.cs` 파일에 있을 때의 MonoScript id |
| `292700ef68995bdb2163e35989fc7eb0` | PugSprite 어셈블리. SpriteObject `1908045241` · SpriteAsset `-217761678` · SpriteAssetManifest `1876717734` |
| `e853a5af7d19630282ad0af7b5dabadc` | TextDataBlock `2108018792` |
| `6f4e9f12d8be4d048a7b574866c31a4f` | `EntityMonoBehaviour` (+ `11500000`) |

### TextDataBlock — 13개 언어 주소는 게임 고정값

`m_localizedTexts.keys`에 13개 언어의 `(m_low, m_high)`가 들어간다. **게임이 정한 값이라
SDK 예제에서 그대로 베끼면 된다.** 어느 항목이 어느 언어인지는 아직 모른다 — SDK 예제는 13개
전부에 같은 영문을 넣는다. 1차 배포가 영어·한국어뿐이므로(기획서 §4) 같은 방식으로 시작하고
7단계에서 구분한다.

`m_header`는 카테고리 문자열(`Items`), `m_shouldBeLocalized: 1`.

### SpriteAsset 의 구조

```
m_staticSpriteData:            변형 0 (기본)
  texture: {fileID: 2800000, guid: <우리 PNG>, type: 3}
  emissiveTexture / normalTexture: {fileID: 0}
  pivot: {x: 0.5, y: 0.5}
  inheritPivot: 1
m_staticVariants: []           변형 1 이상 (파일런 켜짐 = 여기)
```

발광 표현(기획서 §7 "발광 부위")은 `emissiveTexture`로 간다 — 별도 레이어 스프라이트가 아니라
같은 SpriteAsset의 필드다.

## 12. 제작 작업대를 만드는 법 (2026-08-05)

### 레시피를 바닐라 오브젝트에 주입한다

모드는 게임 프리팹을 못 고친다. 대신 **베이킹되는 순간 가로채서** 레시피 버퍼에 밀어 넣는다.

```csharp
[Preserve]
public class … : SingleAuthoringComponentConverter<CraftingAuthoring>
{
    protected override void Convert(CraftingAuthoring authoring)
    {
        if ((ObjectID)ObjectIndex != ObjectID.IronWorkBench) return;
        var id = API.Authoring.GetObjectID("NoBreakZone.Workbench");
        if (id == ObjectID.None) return;                 // DB가 아직 안 올라옴
        EnsureHasBuffer<CanCraftObjectsBuffer>();
        AddToBuffer<CanCraftObjectsBuffer>(new CanCraftObjectsBuffer { objectID = id, amount = 1 });
    }
}
```

**숫자 대신 enum 이름을 쓴다.** `ObjectID` enum에는 명시적 값이 드문드문 박혀 있어
(2,290개 중 205개) 서수를 세면 틀린다. ck-db 값 자체는 정확하다 — `AutomationTable = 4022`가
ConveyorTunnelMod 하드코딩 값과 일치해 교차검증됐다. `IronWorkBench = 4010`.

### 작업대 로직 프리팹 — `CraftingAuthoring`

```
canCraftObjects:
- objectID: 0                        ← 모드 오브젝트는 0
  moddedObjectID: NoBreakZone.Pylon  ← 이름으로 지목한다
  amount: 1
  craftingTime: 3
  hasPrerequisites: 0
  prerequisites: { contentBundlePresent/Absent, …BossKilled 6종 }
```

모드 오브젝트의 숫자 ID는 로드 전엔 존재하지 않으므로 **이름으로 건다.**

### 작업대 그래픽 프리팹 — 상호작용

루트가 `CraftingBuilding` 파생이어야 하고, `InteractableObject` 자식의 UnityEvent가
루트의 `Use()`(E키) / `OnPlayerLeftBuilding()`(벗어남)을 부른다. 둘 다 public이라
**빈 서브클래스로 충분하다** (`Pug.Other/CraftingBuilding.cs:166,173`).

⚠️ **스톡 게임 클래스를 프리팹에서 지목하는 형태가 두 가지다.**

| 형태 | 언제 |
| --- | --- |
| `{fileID: 11500000, guid: <그 .cs의 meta guid>}` | 느슨한 `.cs`. 예: `EntityMonoBehaviour` |
| `{fileID: <이름 MD4 해시>, guid: <어셈블리 guid>}` | dll 컴파일. 예: `InteractableObject` (`-1216031652` / `548e3dd2…`) |

같은 `Pug.Other` 안에서도 갈린다. **`CraftingBuilding`이 어느 쪽인지 알려주는 레퍼런스가 없다** —
그래서 우리 서브클래스를 만들어 첫 번째 형태로 확정시킨다. SDK 예제도 서브클래스를 쓴다.

⚠️ **`m_TargetAssemblyTypeName`은 `<타입>, <어셈블리>` 문자열이다.** 우리 어셈블리 이름은
`NoBreakZone.asmdef`의 `name`, 즉 `NoBreakZone`. **SDK 예제를 그대로 베끼면 안 된다** —
거기엔 `WorkBenchGraphical, ItemExample`이라 적혀 있는데 그 스크립트는 `WorkbenchExample.asmdef`
밑에 있다. 옮기고 안 고친 흔적이다. `preflight.py`가 못 잡는 문자열이다.

## 13. 켜고 끄는 토글은 전부 게임에 있다 (2026-08-05)

**직접 만들 RPC도 커스텀 네트워크 코드도 없다.** `EntityMonoBehaviour.SetVariation(int)` 한 줄이
클라 예측·서버 반영·다른 클라 전파를 전부 한다.

```
그래픽쪽 InteractableObject.onUseActions (E키)
   └─ 우리 메서드 → EntityMonoBehaviour.SetVariation(n)      ← Pug.Other/EntityMonoBehaviour.cs:429
        ├─ variationOverride 기록 (누른 사람은 즉시 반영)
        ├─ variationOverrideUpdateCount++ (단조 증가)
        └─ 고스트면 SetVariationRPC 발송
              └─ 서버 SetVariationSystem                      ← Pug.Other/SetVariationSystem.cs
                   ├─ 게스트 모드 차단 (admin 아니면 무시)
                   ├─ updateCount 가 더 커야만 적용 (순서 뒤바뀜 방지)
                   └─ ObjectDataCD.variation 기록 → NetCode 가 전원에게 복제
```

`EntityMonoBehaviour.variation` 게터는 **서버가 따라잡을 때까지 로컬 override를 돌려준다**
(`:81-92`). 그래서 누른 사람 화면은 지연 없이 바뀐다.

**세이브는 공짜다.** `ObjectDataCD`는 고스트 컴포넌트이자 월드 세이브 대상이다.

### ⚠️ 그래픽 갱신은 우리가 해야 한다

`UpdateGraphicsFromObjectInfo`는 **그래픽 오브젝트가 스폰될 때만** 불린다
(`EntityMonoBehaviour.cs:1128`). 누른 사람은 로컬 override 덕에 바뀌어 보이지만,
**다른 클라이언트는 복제된 `ObjectDataCD`만 바뀌고 아무도 갱신을 안 부른다.**

해법은 `ManagedLateUpdate()` override — 프레임마다 도는 virtual 훅이고
`CraftingBuilding`·`Minecart`·`GoKart`가 이미 그렇게 쓴다. variation이 마지막에 반영한 값과
다르면 `UpdateGraphicsFromObjectInfo(objectInfo)`를 부른다. `objectInfo` 게터가 현재 variation으로
조회하므로 그것만으로 맞는 변형이 잡힌다.

### 발광·이펙트·사운드 (5단계)

**발광은 `emissiveTexture`로 간다.** 변형 0과 1이 **같은 본체 텍스처**를 가리키고
`emissiveTexture`만 다르다. 기획서 §7의 "스프라이트는 한 종류만 만들고 발광 레이어를 켜고 끄는
방식"이 이것이고, 실루엣이 어긋날 수가 없다.

발광 텍스처는 `genassets.py`가 **초안 두 장의 차분에서 생성한다** — `pylon_off`와 `pylon_on`은
알파가 완전히 같고 36픽셀(보석)만 다르다.

**이펙트·사운드는 프리팹으로 못 한다.** `EntityMonoBehaviour.soundOptions`에는
`takeDamageSfx`·`deathSfx`뿐이고 파티클 `spawnOccasion`에도 "변형이 바뀜"이 없다. 코드로 부른다.

```csharp
API.Effects.PlayPuff(int puffId, Vector3 position, int particleCount = 10);
API.Audio.PlaySfx(int sfxTableID, Vector3 position, Transform follow = null,
                  float volume = 1, float pitch = 1, int sfxType = 2);
```

**둘 다 `int`를 받지만 enum 이름으로 캐스팅한다** — `(int)PuffID.AncientBurst`. ObjectID와 같은
이유다. 목록은 `Pug.Base/PuffID.cs`·`Pug.Base/SfxID.cs`, 사운드 이름만 뽑힌 것은
`ck-mods/Docs/CoreKeeper-SfxID-list.txt`.

⚠️ **변형 변화 감지에 "처음 반영" 가드가 필요하다.** 없으면 파일런이 스트리밍될 때마다,
세이브를 켜짐 상태로 불러올 때마다 소리가 난다. 상태를 *맞추는 것*과 상태가 *바뀐 것*은 다르다.

### 변형 → 스프라이트 (미검증 추론)

`SpriteAsset.m_staticVariants[0]`이 변형 1이다 — `SpriteObject.SetVariantByIndex`가
`variantIndex--` 후 `GetStaticVariantHash(index)`를 쓰므로 **0 → 기본, 1 → 변형 0**.
**인게임 확인 항목.** 안 되면 `EntityMonoBehaviour.objectVariants`(변형별 GameObject on/off,
`:1181-1207`)로 바꾼다.

## 14. 아이템과 클라이언트 오버레이 (2026-08-05)

### 설치물이 아닌 아이템은 컴포넌트 3개면 된다

SDK `ItemExample/Sword1.prefab` 기준: `ObjectAuthoring` · `InventoryItemAuthoring` ·
`LocalizationAuthoring`. 그리고

- `graphicalPrefab: {fileID: 0}` — **그래픽 프리팹 없음**
- **SpriteAsset 없음.** 아이콘이 PNG의 Sprite 서브에셋을 직접 가리킨다
- 고스트·물리·상태 authoring 전부 없음

`objectType`은 `Pug.Base/ObjectType.cs`에서 고른다. 손에 들지만 기계적 용도가 없는 도구는
**`KeyItem` (1500)**.

### 모드가 자기 에셋을 런타임에 잡는 법

**`IMod.ModObjectLoaded(Object obj)`가 유일한 통로다.** 번들의 에셋이 하나씩 넘어오고,
**이름으로 골라낸다.** 런타임에 경로나 guid를 받을 방법은 없다.

```csharp
public void ModObjectLoaded(Object obj) {
    if (obj is Sprite s && s.name == "NoBreakZoneRangeMarker") _marker = s;
}
```

레퍼런스: `ck-mods/…/ConveyorTunnelHelperSpriteRegistry.RegisterLoadedObject`.

### 클라이언트 매 프레임 훅과 필요한 접근자

| 필요한 것 | 어디 |
| --- | --- |
| 매 프레임 (클라) | `IMod.Update()` |
| 로컬 플레이어 | `Manager.main.player` |
| 손에 든 물건 | `player.visuallyEquippedContainedObject.objectData.objectID` |
| UI 열림 판정 | `Manager.ui.isShowingMap` · `isAnyInventoryShowing` |
| 클라 월드(ECS) | `API.Client.World` → `GetExistingSystemManaged<T>()` |

⚠️ **바닥에 스프라이트를 깔 때 재질을 어디서 가져오나 — 미해결.** 맨 `SpriteRenderer`는 이
게임 렌더 파이프라인에 맞는 재질이 없다. 레퍼런스 모드는 설치 중에 넘어오는 `PlacementIcon.SR`에서
material·sortingLayer를 복사하는데, **설치 중이 아니면 그 대상이 없다.**
우리는 `FindObjectOfType<PlacementIcon>(true)`로 씬에서 빌려 쓴다 — **인게임 확인 필요.**
못 찾으면 유니티 기본 스프라이트 재질이 쓰여 안 보이거나 벽을 뚫고 그려질 수 있다.

## 15. 커서가 지목한 것을 아는 법 — `ClientInput` (2026-08-05)

design.md §12의 **"커스텀 도구가 커서로 지목한 오브젝트를 알 수 있는가"**에 대한 답: **된다.**
그리고 처음 보인 길보다 훨씬 싸다.

### 처음 보인 길 (쓰지 않음)

아이템 사용에 반응하려면 `EquipmentSlot.UpdateEquipment(…, bool secondInteractHeld, …)`를
Harmony로 패치해야 하는데, **Burst 잡 안이라 `EquipmentUpdateSystem` 전체의 Burst를 꺼야 한다.**
SDK의 `TeleportAfterEating` 예제가 그 방식이고, 예제 주석 자체가 "This slows down the game"이라고
적어뒀다.

### 실제로 쓴 길 — `ClientInput`

`Pug.ECS.Components/ClientInput.cs`. **netcode 입력 컴포넌트라 서버에 이미 복제돼 있다.**

| 필드 | 내용 |
| --- | --- |
| `float2 mouseOrJoystickWorldPoint` | **커서 월드 좌표** |
| `short buttonSetMask` + `IsButtonStateSet(name)` | 버튼 상태 |

`CommandInputButtonStateNames`에 **눌린 순간(Pressed)과 누르고 있음(HeldDown)이 따로** 있다 —
`SecondInteract_Pressed = 256`. 엣지를 우리가 기억할 필요가 없다.

들고 있는 물건: `EquippedObjectCD.equippedSlotIndex` → `ContainedObjectsBuffer[slot]`
(`ChangeVariationWhenPlayerHoldObjectNearbySystem`과 같은 패턴).
클라 쪽에서는 `Manager.main.player.clientInput`으로 같은 것을 읽는다.

**서버 시스템이면 RPC가 필요 없다.** `ObjectDataCD`를 직접 쓰면 NetCode가 복제한다.
4단계의 E키가 클라에서 시작해 `SetVariationRPC`를 보내야 했던 것과 대비된다.

⚠️ **미검증 가정:** `KeyItem`은 `NonUsableSlot`으로 분류되는데
(`PlayerController.GetEquippedSlotTypeForObjectType`), `ClientInput`은 슬롯 로직 이전의 원시
입력이므로 **든 물건과 무관하게 플래그가 설 것**으로 본다. 리모콘이 반응하지 않으면 여기다.

## 16. 다중 타일 보호 판정 (2026-08-05)

### 원점 타일 하나로는 안 된다

`LocalTransform.Position.RoundToInt2()`는 오브젝트의 **원점 타일**일 뿐이다.
1×1이면 그게 유일한 타일이라 같지만, 그보다 크면 틀린다.

점유 타일 계산은 게임 것을 그대로 쓴다 (`Pug.Other/DetectRoomSystem.cs:167-192`):

```csharp
int2 tile = LocalTransform.Position.RoundToInt2();
ObjectInfo info = PugDatabase.GetObjectInfo(objectID, variation);   // 없으면 null
int2 size   = new int2(info.prefabTileSize.x,     info.prefabTileSize.y);
int2 corner = new int2(info.prefabCornerOffset.x, info.prefabCornerOffset.y);
if (em.HasComponent<DirectionCD>(entity))                            // 회전물
    directionCD.GetPrefabOffsetAndTileSize(corner, size, out corner, out size);
tile += corner;
// 점유 = [tile, tile + size)   ← 반열림. 포함 사각형으로 바꾸려면 -1
```

- **`GetObjectInfo`는 없는 id에 `null`을 준다.** 1×1 폴백을 두되 **로그를 남긴다** —
  안 그러면 예전(원점만 보던) 동작으로 조용히 돌아간 것을 알 수 없다
- `variation`을 넘겨야 한다. 변형에 따라 크기가 다른 오브젝트가 있다
- Burst가 아닌 관리 `SystemBase`라 blob 뱅크 없이 관리 API를 쓸 수 있다

### 기획서 §6의 두 규칙은 합집합으로 읽는다 (2026-08-05 결정)

| 규칙 | 원문 |
| --- | --- |
| 다중 타일 | "모든 타일이 **범위** 안에 들어와야 보호된다" |
| 겹침 | "**어느 하나**의 켜진 파일런 범위 안에 있으면 보호된다" |

**타일마다 따로 판정하고, 모든 타일이 어느 파일런이든 하나에 덮이면 보호한다.**
두 파일런의 이음매에 걸친 다중 타일 설치물(상자·침대 등)은 보호된다. 파일런이 하나면
"한 파일런 기준"과 결과가 같다.

### 거는 쪽과 푸는 쪽이 같은 판정을 써야 한다

둘이 어긋나면 **보호는 걸리는데 영원히 안 풀리는** 오브젝트가 생긴다. 파일런을 꺼도 안 부서지고,
원인을 인게임에서 알아낼 방법이 없다. `NoBreakZoneProtectionSystem`은 후보 판정과
`ReleaseUncovered`가 **같은 메서드**를 부른다.

## 17. 현지화와 설정 (2026-08-05)

### 현지화 = TextDataBlock. `Localization.csv`는 런타임에 안 읽힌다

공식 문서 `how-to-localize-your-mod.md`가 명확하다.

- **런타임 조회 키는 TextDataBlock의 이름**이고, `Header`는 `Items`여야 한다
- `.csv`는 **유니티 에디터에서 임포트할 때만** 쓰는 편의 포맷이다. 문서가 임포트 후
  "다른 디렉터리로 옮겨 백업하라"고 안내한다
- 13개 슬롯을 다 채우면 어느 언어에서도 이름이 뜬다 (영어로라도). 비우면 그 언어에서 빈칸

**즉 `Localization/` 폴더는 만들 필요가 없다.** 기획서 §4의 "구조는 13개 언어를 받을 수 있게"는
3단계에 이미 충족돼 있었다.

### ⚠️ 어느 슬롯이 어느 언어인지 모른다 — 윈도우 필요

13개 주소는 게임 번들 안 `LanguageDataBlock` 에셋의 guid다.

- 13개를 전부 guid로 환원해 `ck-db`·`ck-mods`·CoreLib·SDK 예제를 검색 → **0건**
- 레퍼런스 모드 중 **비영어로 현지화한 것이 없다**

**알아내는 법:** 유니티 Scriptable Data Editor로 TextDataBlock을 열면 슬롯마다 언어 이름이 보인다.
한국어의 0-기준 인덱스를 `genassets.py`의 `LANGUAGE_SLOTS`에 적으면 된다.

### 설정은 `API.Config` — `Conf/` 폴더는 우리가 안 만든다

```csharp
IConfigEntry<T> API.Config.Register<T>(mod, section, description, key, defaultValue);
entry.Value
```

파일 위치·형식을 게임이 관리한다. **레퍼런스 모드 중 이걸 쓰는 사례는 없다** — 공식 API지만
실사용례 없이 문서만 보고 쓴 것이다. 등록 실패 시 상수로 폴백하게 해뒀다.

### "몹 피해 차단"에 새 메커니즘이 필요 없다

8·9장의 두 컴포넌트가 **서로 다른 피해 경로**를 막는다는 것이 그대로 설정이 된다.

| 컴포넌트 | 막는 것 |
| --- | --- |
| `IndestructibleCD` | **플레이어발** 피해 (`PlayerController.DealDamageToObject`가 이것만 본다) |
| `DontDestroyOnZeroHealthCD` | **모든** 피해원의 파괴 (`SetEntitiesDestroyedSystem` 단일 관문) |

**켬 = 둘 다. 끔 = `IndestructibleCD`만.** 미검증 추론이다 — 끈 상태를 인게임에서 본 적 없다.

## 18. 제작창은 벤치당 18칸이고, 화살표가 있으면 붙일 수 없다 (2026-08-06)

작업대 레시피가 인게임에서 안 뜨는 문제를 며칠 붙잡았다. **원인은 우리 코드가 아니라 제작 UI의
구조였다.** 다시 헤매지 않으려면 이 장이 필요하다.

### 창 3개 × 6칸 = 벤치당 18개가 상한

`SimpleCraftingUIContainer`는 `List<SimpleCraftingUI>`를 3개 들고 있고, `MAX_RECIPES_PER_UI = 6`
이다. `ShowCraftingUI`가 슬롯 범위를 6칸씩 끊어 **사용 가능한 레시피가 있는 청크마다** 창을
하나 연다. 창이 모자라면 이렇게 찍고 그 청크만 안 그린다(패널 전체가 죽지는 않는다):

```
Not enough SimpleCraftingUIs in SimpleCraftingUIContainer to show all recipes.
Needed at least 4, but only have 3.
```

공식 모딩 문서도 같은 말을 한다 — *"each workbench can have AT MOST 18 slots. You cannot exceed
this value. As such this approach isn't too scaleable."*

### 화살표 = `IncludedCraftingBuildingsBuffer`

제작창 왼쪽의 ▲▼와 그 사이 아이콘이다. **다른 작업대의 레시피 묶음을 페이지처럼 넘긴다.**

`CraftingBuilding.OnOccupied`가 이 버퍼를 순회하며 구역을 만들고, `amountOfCraftingOptions`를
누적해 `startSlotIndex`/`endSlotIndex`를 잡는다. `ShowCraftingUI`는 **선택된 구역의 범위만**
걷는다(`Manager.ui.GetCraftingCategoryWindowInfo()`).

**결론: 화살표가 있는 벤치에는 목록 끝에 붙여봐야 소용없다.** 어느 구역에도 안 들어가서 걸리지
않는다. 화살표가 없으면 범위가 `0 ~ recipes.Length` 전체라 붙이면 그려진다.

구역이 어떻게 채워지는지는 `CoreLib`의 베이커 재구현본이 보여준다
(`CoreLib/.../ModCraftingAuthoring.cs`) — **첫 항목이 그 벤치 자신**이고 이어서 흡수한 벤치들이
붙는다. `moorowl/ItemBrowser`가 `.Skip(1)`로 읽는 것도 같은 이유다. 굽는 시점에 정해지므로
**근접성과 무관하다.**

### 벤치별 실측 (위키 본문 직접 카운트, 2026-08-06)

| 벤치 | 자기 레시피 | 화살표 | 비고 |
| --- | --- | --- | --- |
| Basic Workbench | 17 / 18 | 없음 | |
| Copper Workbench | 18 / 18 | 있음 | Basic 흡수 |
| Tin Workbench | 18 / 18 | 있음 | Basic·Copper 흡수 |
| **Iron Workbench** | **18 / 18** | **있음** | Basic·Copper·Tin 흡수 → 버퍼 72 |
| Electronics Table | 18 / 18 | 없음 | |
| **Automation Table** | **6 / 18** | **없음** | 우리가 쓰는 벤치 |
| Jewelry Workbench | 6 / 18 | 없음 | 철 단계 |
| Key Casting Table | 7 / 18 | 없음 | 철 단계 |

**18이 게임이 벤치를 채우는 기준값이다.** 네 벤치가 정확히 18에서 멈춘다.

### 손 제작도 같은 UI, 같은 18칸

`OpenPlayerInventory()`가 `SetActiveCraftingHandler(playerCraftingHandler)` 후 같은
`CraftingType.Simple` 경로를 탄다. 다만 `UIManager.GetCraftingBuilding()`이 플레이어 제작에서는
null을 반환하므로 **구역이 없다** — 즉 붙이기가 통한다. `budak7273/HandCraftWoodBridges`가
`[EntityModification(ObjectID.Player)]`로 여기에 넣는다.

### 빈칸에 끼워넣기보다 끝에 붙이는 쪽이 안전하다

공식 문서와 여러 모드(`limoka/DummyMod`, `Foxcapades/ck-tweaks`, `germanoeich/Cornucopia`)는
`ObjectID.None`인 슬롯을 찾아 `buffer[i] = ...`로 덮어쓴다. 하지만 **그 빈칸은 진행도 필터가
만든 자리일 수 있다** — `AvailableRecipesFromContentBundlesSystem`이 0.2초마다
`ObjectPropertiesCD`의 `Crafting/unfilteredRecipes`를 읽어 `[0, 원본길이)` 구간을 다시 쓰면서
조건 미달 레시피를 `None`으로 만든다. 거기에 우리 것을 꽂으면 되돌려진다.

**끝에 `Add`하면 그 구간 밖이라 안 덮인다.** ConveyorTunnelMod가 자동화 테이블에 그렇게 한다.

### 레퍼런스 위치

| 무엇 | 어디 |
| --- | --- |
| 공식 권장 방식 | `CoreKeeperMods/Core-Keeper-Docs` → `.../obtaining-items/adding-your-items-to-crafters.md` |
| 자동화 테이블에 붙이는 컨버터 | `ck-mods/.../ConveyorTunnelRecipeInjectionConverter.cs` |
| 카테고리를 직접 만드는 예 | `ck-mods/.../ConveyorTunnelAdvancedAutomationRecipeInjector.cs`, `ck-mods/.../DimensionPortalRecipeInjector.cs` |
| 베이커 재구현본 | `CoreKeeperMods/CoreLib` → `.../Entity/Scripts/Component/ModCraftingAuthoring.cs` |
| 몹 드롭으로 주는 길 | `CoreLib` LootDrop 모듈, `ReishyouSose/.../DropTrophySystem.cs` |

### 상인은 탈출구가 아니다

재료를 주고 물건을 받는 NPC는 `CraftingCD{Simple}` + `CanCraftObjectsBuffer`를 가진
`ObjectType.Creature`다(`ItemBrowser/.../Trading.cs`의 판별식). **작업대와 같은 버퍼·같은 UI·같은
18칸**이고 대상 ID만 다르다. 자판기(`VendingMachineItemBuffer`)는 별개 구조지만 여기 쓰는
모드가 GitHub 전체에 0건이다.

## 19. 이름·상호작용·번들이 넘겨주는 것 (2026-08-06)

작업대를 처음 설치해보고 드러난 세 가지. 전부 **인게임 로그와 SDK 예제 대조**로 확정했다.

### 19-1. 아이템 텍스트는 오브젝트 이름으로 찾는다 — 넷이 같아야 한다

툴팁에 `missing: Items/NoBreakZone.Workbench`가 떴다. **그 문자열이 게임이 요구한 term이고,
`NoBreakZone.Workbench`는 우리 `ObjectAuthoring.objectName`이다.** 우리 TextDataBlock은
`NoBreakZoneWorkbench`(점 없음)라 답할 수 없었다.

SDK 예제에서는 이게 안 드러난다 — `objectName`·`termKey`·TextDataBlock의 `m_Name`·그 에셋의
**파일명이 전부 `MyNewWorkbench1` 하나**다.

> **그 TextDataBlock을 guid로 참조하는 파일은 예제 전체에 없다** — 자기 `.meta`뿐이다
> (`grep -rl <guid>`로 확인). 즉 프리팹과 텍스트를 잇는 것은 **이름뿐**이고, 이름이 다르면
> 연결 자체가 없다. 17장의 "TextDataBlock이 곧 현지화"에 이 조건이 빠져 있었다.

**규칙: `objectName` = `termKey` = TextDataBlock `m_Name` = 그 에셋 파일명.**
넷 중 `objectName`만은 못 바꾼다 — 세이브에 들어간다(CLAUDE.md §5). 나머지 셋을 거기 맞춘다.

### 19-2. E가 먹으려면 로직 프리팹에 `Interaction.LocalInteractableAuthoring`이 있어야 한다

설치한 작업대에 E를 눌러도 아무 반응이 없었고, **예외도 로그도 없었다.**

찾은 방법: SDK의 동작하는 작업대(`Examples/WorkbenchExample/Workbench/MyNewWorkbenchLogic.prefab`)와
우리 로직 프리팹의 `m_Script` 참조를 `GameData/script_guids.csv`로 역해석해 전수 대조했다.
19개 vs 17개, 차이는 딱 둘 — `RotationAuthoring`(우리가 의도적으로 뺀 것)과
**`Interaction.LocalInteractableAuthoring`**.

| 어디 | 무엇을 하는가 |
| --- | --- |
| 그래픽 프리팹 `InteractableObject` | E를 눌렀을 때 **무엇을 부를지** (UnityEvent) |
| 로직 프리팹 `Interaction.LocalInteractableAuthoring` | **이 엔티티가 상호작용 대상이라는 것 자체** |

우리는 앞의 것만 갖고 있었다. 필드는 두 개(`useSecondInteraction`, `interactSubIndex`),
어셈블리는 `Interaction.Authoring`.

> **`m_TargetAssemblyTypeName`은 범인이 아니다.** UnityEvent의 persistent call은 `m_Target`이
> 살아 있으면 **그 객체의 실제 타입**에서 메서드를 찾고 이 문자열을 쓰지 않는다. SDK 예제 자신이
> `WorkBenchGraphical, ItemExample`이라는 틀린 어셈블리명을 달고도 동작한다(그 스크립트는
> `WorkbenchExample` 아래에 있다). **이 문자열을 다시 용의선상에 올리지 않는다.**

### 19-3. 번들은 모드에 Sprite를 하나도 넘겨주지 않는다

`IMod.ModObjectLoaded`로 들어오는 오브젝트를 **전량 찍어봤다.** 나오는 것은
**Texture2D · GameObject · ScriptableObject뿐이고 Sprite는 0건**이다.

`.meta`의 `spriteMode: 1`이 에디터에서 만드는 Sprite 서브에셋은 **이 경로로 오지 않는다.**
그래서 이름으로 Sprite를 기다리던 렌즈의 범위 마커는 영원히 못 받는다
(`no 'NoBreakZoneRangeMarker' sprite loaded`).

→ Texture2D를 받아 `Sprite.Create`로 직접 만든다. 텍스처를 **참조**할 뿐 픽셀을 읽지 않으므로
임포터의 `isReadable: 0`과 무관하다. `pixelsPerUnit`은 16이어야 한다(11장).

⚠️ **`InventoryItemAuthoring.icon`은 `fileID: 21300000`, 즉 그 Sprite 서브에셋을 가리킨다.**
번들 내부 참조라 위 경로와 다를 수 있어 **단정하지 않는다** — 아이콘이 실제로 그려지는지는
인게임에서 눈으로 확인한다.

## 20. 디컴파일로 확정한 네 가지 (2026-08-07)

**이 장은 추측으로 세 판을 날린 뒤에 썼다.** 파일런 텍스처 하나에 사람 플레이 세션 세 번을 썼고,
세 번 다 원인은 YAML을 보고 세운 그럴듯한 가설이었다. 게임 DLL은 `Assets/Plugins/CoreKeeper/`에
처음부터 있었다. **`dotnet tool install -g ilspycmd` 한 줄이면 30분 만에 끝났을 일이다.**

> 교훈: 게임의 동작이 걸린 문제는 **레퍼런스에 사례가 없으면 코드를 열어본다.**
> "레퍼런스 어디에도 이렇게 하는 것이 없다"는 반증이 아니라 **신호**다.

### 20-1. 모드 오브젝트에는 `ObjectTypeCD`가 없다

`Pug.ECS.Conversion.dll`에 `ObjectTypeCD`를 붙이는 곳은 **한 군데뿐**이다.

| 컨버터 | 붙이는 것 | 누가 타는가 |
| --- | --- | --- |
| `EntityMonoBehaviourDataConverter` | `ObjectDataCD` · `ObjectCategoryTagsCD` · **`ObjectTypeCD`** | 바닐라 (`EntityMonoBehaviourData`) |
| `ObjectConverter` | `ObjectDataCD` · `ObjectCategoryTagsCD` | **모드 (`ObjectAuthoring`)** |

보호 시스템 쿼리가 `ObjectTypeCD`를 `All`에 두고 있어서 **이 모드가 추가한 모든 오브젝트가 규칙이
돌기도 전에 걸러졌다.** 제작대가 자기 파일런 범위 안에서 부서지는데 `PROTECT`도 `skip`도 안 찍히던
이유다. → 오브젝트 타입은 **`PugDatabase`에서** 읽는다. 바닐라·모드 양쪽에 답한다.

### 20-2. 변형은 프리팹 하나당 하나다 — 그리고 없으면 0으로 폴백한다

```
PugDatabase.TryGetObjectInfo(id, out info, variation):
    (id, amount, variation) 조회 → 있으면 반환
    variation = 0 으로 다시 조회 → 반환        ← 조용한 폴백
```

`UpdateEntityMonos`가 `objectsByType`를 채우는 단위는 **authoring 프리팹 하나**다.
우리는 프리팹이 하나(variation 0)뿐이어서 `GetObjectInfo(파일런, 1)`이 **variation 0짜리를
돌려줬고**, 그래서 `EntityMonoBehaviour`가 보는 `info.variation`은 언제나 0이었다.
DB 실측도 같다 — `objectInfos=2880` vs `unique=2283`, 차이가 변형 항목이다.

**변형 하나당 로직 프리팹 하나.** 같은 `objectName`을 쓰면 같은 ObjectID를 받는다
(`ObjectAuthoring.TryGetPreferredObjectIndex`가 이름으로 조회). `ObjectConverter`가 "name" 속성을
`variation == 0`일 때만 쓰는 것이 이 구조를 반대편에서 말해준다.

### 20-3. 변형된 모습은 스프라이트 슬롯이 아니라 **GameObject**다

`SpriteAsset.m_staticVariantLookup`은 `StringToHash(변형.GetName(...))` — **이름 해시**로 만들어지고,
`SetVariant`를 부르는 것은 **스프라이트 방향과 애니메이션**이다. 오브젝트의 `variation`은 여기 안 닿는다.

변형이 그래픽에 닿는 유일한 지점인 `EntityMonoBehaviour.UpdateGraphicsFromObjectInfo`는
`objectVariants`만 훑는다 — **일치하는 항목의 GameObject를 켜고 나머지는 끈다.** 스프라이트는
건드리지 않는다.

→ 변형마다 **SpriteObject 하나 + SpriteAsset 하나**를 두고 `objectVariants`로 갈아끼운다.
(SDK 예제의 `m_staticVariants`는 **방향** 변형이고 `SpriteVariationFromEntityDirection`이 쓴다)

### 20-4. 타일은 `IndestructibleCD`를 안 본다 — 그래서 오히려 안전하다

`TileDamageSystem` (`Pug.Other.dll`):

- **`IndestructibleCD` 참조 0건.** 설치물에 쓰는 그 컴포넌트는 벽·바닥에 안 통한다
- `HealthChange`를 공용 `HealthChangeBuffer`에 넣는다 → 설치물과 **같은
  `SetEntitiesDestroyedSystem` 관문** → `DontDestroyOnZeroHealthCD{disabled=false}`로 막힌다
- `[UpdateInGroup(PredictedSimulationSystemGroup)]` — **클라이언트에서도 예측 실행**.
  9장의 유령 상자와 같은 조건이라 반드시 양쪽 월드에 걸어야 한다

**복제가 안 나는 이유**: 벽 41종은 `lootTable=1`·`lootOnDmg=0` — 전리품이 **파괴 시점**에만 나온다.
파괴를 막으면 전리품이 아예 안 나온다. 복제는 "안 죽으면서 계속 뱉는" 것이라야 성립하고,
그건 `DropsLootWhenDamagedCD`·드릴 대상·광석의 성질이다. 그 넷을 계속 제외한다.

기타: `API.Effects.PlayPuff(puffId, position, particleCount)` — **크기는 puff 종류에 내장**돼 있고
인자로 조절되는 것은 입자 수뿐이다. `TileType`은 `PugTilemap` 네임스페이스(`ore = 129`).

## 21. 타일은 맞을 때만 존재한다 — 프레임 순서 경합 (2026-09-05)

**증상.** 켜진 파일런 구역 안에서 폭발성 무기를 터뜨리면 깔아둔 바닥재가 사라지고 **아이템으로
떨어졌다.** 같은 바닥을 곡괭이로 치면 멀쩡했다. 둘은 같은 보호를 지나므로 원인은 규칙이 아니라
**시각**이었다.

### 21-1. 타일 피해 엔티티는 한 프레임 뒤에 태어나 그 프레임에 죽는다

타일은 평소 엔티티가 없다. 맞는 순간 `TileDamageSystem`이 그 칸에 하나 만드는데,
`BeginSimulationEntityCommandBufferSystem`을 거치므로 **다음 프레임 맨 앞**에 실체화되고
`InitialHealthChange`가 이미 켜진 상태다. 그리고 그 프레임 안에서 죽는다.

```
BeginSimulationEntityCommandBufferSystem   ← 타일 피해 엔티티가 여기서 생긴다
GhostSimulationSystemGroup
PredictedSimulationSystemGroup (OrderFirst)
    InitialHealthChangeSystem → UpdateHealthFromBufferSystem → SetEntitiesDestroyedSystem
...SimulationSystemGroup 일반 구간             ← 우리 두 시스템이 있던 자리. 이미 늦었다
```

**곡괭이가 되던 이유도 같은 사실에서 나온다.** 곡괭이 피해는 `DamageReductionCD.maxDamagePerHit`에
잘려 한 방에 못 죽인다. 살아남은 피해 엔티티는 체력이 다 찰 때까지 남으므로
(`TileDamageSystem.ApplyDamageToExistingDamageEntitiesJob` 꼬리에서 `health >= maxHealth`일 때만
삭제) 다음 프레임에 우리가 표식을 붙였다. 폭발은 `bypassMaxDamagePerHit = true`로 넣어 상한을
무시하고 태어난 프레임에 끝낸다.

| 근거 | 위치 |
| --- | --- |
| 폭발이 상한 무시로 타일 피해 기록 | `ExplosionDamageSystem` 타일 루프 |
| 엔티티 생성 + `InitialHealthChange` 즉시 세팅 | `TileDamageSystem.CreateNewTileDamageEntitiesJob` |
| 그 플래그가 상한을 무력화 | `UpdateHealthFromBufferSystem` (`!bypassMaxDamagePerHit` 조건) |
| 우리가 기대는 유일한 관문 | `SetEntitiesDestroyedSystem` (`DontDestroyOnZeroHealthCD.disabled` 검사) |
| 예측 그룹이 `OrderFirst` | `Unity.NetCode/GhostPredictionSystemGroup.cs` |

### 21-2. `BeforePredictedSimulationSystemGroup`에 넣으면 안 된다 — 동전던지기다

게임의 `ImmunityZoneSystem`이 사는 그룹이라 가장 자연스러워 보이는 자리인데, **위험하다.**

- `BeginSimulationEntityCommandBufferSystem` — `[UpdateInGroup(SimulationSystemGroup, OrderFirst)]`
  **그것뿐이다.** 다른 순서 제약이 하나도 없다.
- `GhostSimulationSystemGroup` — `OrderFirst` + `UpdateBefore(FixedStep)` + `UpdateBefore(Predicted)`.
  ECB와의 연결이 **없다.**
- `BeforePredictedSimulationSystemGroup` — `OrderFirst` + `UpdateAfter(Ghost)` + `UpdateBefore(Predicted)`.

즉 ECB와 그 그룹 사이에는 **제약 경로가 없다.** 유니티의 `ComponentSystemSorter`는 그런 쌍을
시스템 타입 해시로 타이브레이크한다 — 결정적이지만 임의적이고, 모드가 걸 도박이 아니다. 절반의
확률로 엔티티가 아직 없는 프레임에 들어가 **수정이 아무 일도 안 하게 된다.**

**그래서 제약을 직접 적는다.** `[UpdateInGroup(SimulationSystemGroup, OrderFirst = true)]` +
`[UpdateAfter(BeginSimulationEntityCommandBufferSystem)]` +
`[UpdateAfter(GhostSimulationSystemGroup)]` + `[UpdateBefore(PredictedSimulationSystemGroup)]`.
`OrderFirst`가 같은 정렬 버킷에 넣어 주고(버킷을 넘는 제약은 버려진다) 나머지는 직접 간선이라
그래프의 다른 무엇도 뒤집지 못한다.

`Editor/verify.ps1`이 이 순서를 유니티 자체 정렬기로 확인한다. **수정 전 속성으로 되돌리면 그
시스템이 예측 그룹(3)보다 뒤인 4번으로 정렬되는 것이 실제로 관찰된다.**

### 21-3. 체력을 안 거치는 타일 편집이 두 갈래 더 있다

`TileUpdateBuffer`(`{Command command; int2 position; TileCD tile;}`)에 직접 쓰는 경로들이다.
엔티티도 체력도 파괴 관문도 없어서 피해 파이프라인의 어떤 보호도 닿지 않는다.

1. **폭발의 지면 파헤침** — 반경 안에서 `GetTop`이 `ground`인 칸마다 `Add dugUpGround`.
   단, 반경 안에 `IndestructibleCD`가 켜진 엔티티가 하나라도 있으면 이 블록 전체를 건너뛴다
   (`CollidesWith = 1024` = `DefaultLowTriggerNonBlocking`). 보호된 기지에서는 우리가 붙인
   `IndestructibleCD` 때문에 자주 안 터진다 — 즉 **미관 문제에 가깝다.**
2. **삽의 바닥재 걷기** — `ShovelSlot` → `PlayerController.DigUpTile` → `EntityUtility.RemoveTile`.

> ⚠️ **삽은 이 버퍼에서 막으면 안 된다 — 아이템이 복제된다.**
> `DigUpTile`은 타일 제거를 `TileUpdateBuffer`에, 아이템 드롭을 **별도 `EntityCommandBuffer`**에
> 넣는다. 우리가 지울 수 있는 건 앞의 것뿐이라, 막으면 **바닥은 남고 아이템은 떨어진다.**
> 무한 반복 가능 = 기획서 §6이 절대 금지한 자원 복제다. 삽을 막으려면 결정이 내려지는 곳에서
> 막아야 한다 (21-5).

### 21-4. `Clear` + `Add` 짝을 깨면 칸이 비어버린다

`EnsureSameGroundTileBeneathEntitySystem`은 설치물 밑 지면을 맞출 때 같은 칸에 `Clear` 다음
`Add`를 **짝으로** 넣는다. 게임의 `UpdateSubMapCommon.FilterUpdates`는 버퍼를 **거꾸로** 훑기
때문에(`for (int i = length - 1; i >= 0; i--)`) 그 짝은 그대로 살아남는다 — `Add`가 먼저 처리되고
`Clear`는 그 뒤에 집합에 들어간다.

따라서 `Clear`를 통과시키면서 뒤따르는 `Add`만 지우면 **그 칸이 빈다.** 우리 필터는 같은 위치에
앞선 `Clear`가 있으면 그 `Add`를 건드리지 않는다.

(참고: `IsIgnoreClear()`는 `roofHole` 하나뿐이다.)

### 21-5. 게임에는 우리가 원하던 장치가 이미 있다 — 하지만 세이브에 남는다

`TileDamageSystem`은 피해 엔티티를 만들기 전에 `tileLookup.HasType(position, TileType.immune)`을
본다. **`immune` 타일이 깔린 칸에는 피해 엔티티가 아예 생기지 않는다** — 곡괭이·폭발·몹·드릴
전부, 양쪽 월드에서, 근원에서. 그리고 `ImmunityZoneSystem`(`BeforePredictedSimulationSystemGroup`)이
`ImmunityZoneCD{radius, offset, useRectangularBounds, rectangularWidth/Height, removeImmunityZone}`를
보고 정확히 그 타일을 깔아 준다. `useRectangularBounds`는 **정사각형**이라 기획서 §6의 모양과 같다.
`HoeSlot`·`ShovelSlot`도 `immune`을 보고 스스로 물러난다.

**그런데 `immune`은 타일맵에 기록되므로 월드 세이브에 들어간다.** 파일런을 켠 채 모드를 지우면
그 땅은 영구히 안 부서진다. CLAUDE.md §6의 "세이브 데이터 구조에 영향"에 해당하므로
**결정 전에는 쓰지 않는다.** 삽 차단과 호미 차단을 한 번에 해결하는 유일한 길이라 열어 둔다.

## 22. 엔티티에 피해를 주는 법 — 필드 셋을 빼먹으면 조용히 아무 일도 안 난다 (2026-09-08)

8장은 모든 피해원이 `HealthChangeBuffer`로 수렴한다는 것까지 밝혔다. **거기에 무엇을 넣어야 실제로
피해가 되는지**는 안 적혀 있었고, 그 틈에서 자가 테스트 대조군 두 개가 조용히 죽었다.

`UpdateHealthFromBufferSystem`(`Pug.Other.dll`)을 디컴파일하면 관문이 둘이다.

### 22-1. `applyToNonPredicted` — 이걸 빼면 파괴 경로에 도달조차 못 한다

```csharp
bool flag = !healthChange.applyToNonPredicted
            && simulationLookup.HasComponent(entity)
            && !simulationLookup.IsComponentEnabled(entity);
...
if (flag)
{
    if (healthCD.health > -1 * num && num < 0)
    {
        healthCD.health = math.clamp(healthCD.health + num, 0, healthCD.maxHealth);
        DoDamageEffects(...);
    }
    continue;   // ← 전리품·파괴 판정을 통째로 건너뛴다
}
```

`Simulate`가 **꺼진** 엔티티에 `applyToNonPredicted = false`로 피해를 쓰면, **엔티티가 살아남을
때만** 피해가 적용되고(`health > -amount`) 그 뒤 `continue`한다. 즉 **죽일 수 있는 피해는 아예 적용되지
않는다.** -9999를 넣으면 조건이 `health > 9999`가 되므로 체력 10짜리 작업대는 **피해를 0 받는다.**

로그에도 아무것도 안 남는다. 그래서 "보호가 동작한다"와 "피해가 안 들어갔다"가 구분되지 않는다 —
**대조군이 없으면 알아챌 수 없는 종류의 실패다.**

### 22-2. `bypassMaxDamagePerHit` — 없으면 금액이 잘린다

```csharp
if (num < 0 && !healthChange.bypassMaxDamagePerHit
    && damageReductionGroup.HasComponent(entity)
    && damageReductionGroup[entity].maxDamagePerHit > 0)
{
    num = math.max(num, -damageReductionGroup[entity].maxDamagePerHit);
}
```

벽이 곡괭이 한 방에 안 죽는 그 상한이다. 폭발이 그걸 무시하는 이유가 `ExplosionDamageSystem`이
이 플래그를 세우기 때문이고(8장·21장), 우리도 폭발 흉내를 내려면 똑같이 세워야 한다.

### 22-3. 그래서 올바른 모양

```csharp
new HealthChange
{
    entity = target,
    amount = -damage,
    applyToNonPredicted = true,     // 없으면 파괴 판정 자체가 안 일어난다
    bypassMaxDamagePerHit = true,   // 없으면 금액이 maxDamagePerHit으로 잘린다
    damagedByExplosion = true,      // 폭발 모양 (전리품·이펙트 경로)
}
```

`HealthChange`의 나머지 필드(`causedByEntity`, `skipLootDropOnDestroy`, `pullLootToPlayer` 등)는
전리품과 연출을 정한다. 파괴 여부에는 관여하지 않는다.

### 22-4. 어디서 물렸나

2026-09-08 첫 인게임 판정에서 `placeable-outside-breaks`와 `pylon-off-breaks`가 FAIL로 나왔다.
둘 다 **대조군**이었고, 둘 다 아무 보호도 없는 엔티티였다. 통과한 케이스는 전부 타일
(`TileDamageBuffer`, 이미 `bypassMaxDamagePerHit`을 넘긴다)이고 실패한 둘만 `HealthChangeBuffer`에
직접 썼다 — 갈리는 선이 정확히 여기였다.

> **교훈은 필드가 아니라 대조군이다.** 계측기가 고장 났는데도 "보호 대상이 살아남았다"는 초록으로
> 보였을 것이다. 대조군이 빨간 상태의 초록을 SKIP으로 처리하게 해 둔 규칙(21장)이 이번에도
> 잘못된 결론을 막았다.

## 23. 곡괭이 광석은 벽 안에 들어있다 (2026-09-10)

이 사실 하나를 몰라서 **오진을 두 번** 했다. `ore-inside-still-breaks`가 인게임에서 세 판 연속
실패한 것을 자원 복제 버그로 보고했는데, 버그도 아니었고 복제도 아니었다.

### 23-1. 광석은 타일 위의 물건이 아니라 벽 속의 자원이다

`PlayerController`가 그렇게 다룬다.

```csharp
// 광석을 맞았을 때, 같은 자리의 '벽'을 찾아본다
if (tileType.IsContainedResource()
    && playerAttackShared.tileAccessor.GetType(position.RoundToInt2(), TileType.wall, out var tileCD))

// 채굴 판정은 벽과 '담긴 자원'을 한 부류로 본다
if (top.tileType != TileType.wall && !top.tileType.IsContainedResource())
```

**체력을 들고 있는 엔티티는 벽이다.** 2026-08-07 결정으로 벽이 보호 대상이 됐으므로,
**벽이 안 부서지면 그 안의 광석도 안 나온다.** 모드가 광석을 보호한 것이 아니라 벽을 보호한 것이다.

### 23-2. 규칙은 광석에 닿지도 못한다

`object_flags.csv`의 **광석 타일 10종이 전부 `health = 0`**이고,
`NoBreakZoneProtectionRule`의 첫 줄이 `if (!hasHealth) return false;`다. 로그에 `PROTECT <광석>`
줄이 한 번도 없던 이유이고, "규칙이 광석을 보호하고 있다"는 가설이 애초에 성립할 수 없던 이유다.

### 23-3. 복제는 광석이 아니라 **덩어리**의 성질이다

복제는 **"안 죽으면서 계속 뱉는"** 것이라야 성립한다. 결정적인 것은 `lootOnDmg`다.

| | `lootOnDmg` | 보호하면 |
| --- | --- | --- |
| 곡괭이 광석 (벽 속) | **0** | 맞아도 아무것도 안 나옴 → **복제 아님**, 그냥 안 캐짐 |
| 드릴 광석 (덩어리) | **1** | 드릴이 때릴 때마다 나옴 → **이것이 진짜 복제** |

기획서 §6이 지목한 것도 정확히 덩어리다("드릴이 광석 덩어리를 캐는 경우가 위험하다"). 그리고
기획서 271줄("지형에 박힌 광석 = 보호한다")과 282·476줄("광석은 제외")은 **모순이 아니다** —
앞은 *관찰되는 동작*, 뒤는 *규칙*을 말하고 둘 다 맞다.

> **교훈은 용어였다.** "광석"이라는 한 단어가 성질이 정반대인 두 물건을 가리키고 있었고, 그것을
> 구분하지 않은 채로 코드를 읽어서 없는 버그를 두 번 쫓았다. 사람이 "안 캐져도 괜찮은 거 아냐?"라고
> 되물어 준 것이 그 구분을 강제했다.

## 24. 우리 오브젝트가 곡괭이에 안 부서지던 이유 (2026-09-10)

파일런을 끄고 태양석 곡괭이로 한참 때려야 회수됐다. 체력은 10인데 그렇다.

`genassets.py`가 `DamageReductionAuthoring`에 **`maxDamagePerHit: 1`**을 써넣고 있었다.

```csharp
if (num < 0 && !healthChange.bypassMaxDamagePerHit
    && damageReductionGroup.HasComponent(entity)
    && damageReductionGroup[entity].maxDamagePerHit > 0)
    num = math.max(num, -damageReductionGroup[entity].maxDamagePerHit);
```

**한 대에 1 피해로 잘린다.** 채굴은 `bypassMaxDamagePerHit`을 안 세우므로 어떤 도구를 써도
**최소 10대**다. 게임은 이 값이 **0보다 클 때만** 자르므로 0이 "상한 없음"이다.

거기에 `hasHealthRegeneration: 1` + `100%/5초`까지 있었다. `HealthConverter`는
`healInCombatAsWell`이 false면 `StopHealthRegenOnDamageTakenCD`를 붙이므로 **때리는 동안은 멈추고
손을 떼면 5초 뒤 만피로 돌아온다.** 둘이 겹쳐서 회수가 끝나지 않았다.

체력 자체는 10이 맞다 — `ComputeMaxHealth`의 레벨 스케일링은 프리팹에 `AreaLevelAuthoring`이
있을 때만 걸리는데 우리 것에는 없다.

**보호와는 무관한 값들이다.** 보호는 `IndestructibleCD`·`DontDestroyOnZeroHealthCD`로 걸린다.

### 24-1. 상한을 풀었더니 주먹 한 방이 됐다 — 숫자를 지어내지 말고 베낀다 (2026-09-10)

위 수정(상한 0 = 없음)을 넣자 이번엔 **맨손 한 대에 부서졌다.** 10 체력에 상한이 없으니 후반
캐릭터의 주먹 한 방이 10을 넘는다. 10도 1도 지어낸 숫자였던 게 문제다.

기획서 §4는 "일반 설치물처럼 회수"라 했고, 읽을 수 있는 바닐라 모양 설치물이 SDK에 하나 있다 —
`Examples/WorkbenchExample/Workbench/MyNewWorkbenchLogic.prefab`:

| 값 | SDK 작업대 | 우리 1차 | 우리 2차 | **지금** |
| --- | --- | --- | --- | --- |
| `maxHealth` | **2** | 10 | 10 | **2** |
| `maxDamagePerHit` | **1** | 1 | 0 | **1** |
| `hasHealthRegeneration` | **1** (100%/5초) | 1 | 0 | **1** |

즉 바닐라 설치물은 **"아무 도구로나 두 대, 몇 초 안에"**다. 주먹도 주석 곡괭이도 태양석도 같다.
재생은 때리는 동안 멈추고 손을 뗀 5초 뒤 만피가 되므로, 지나가다 한 대 스친 것은 회수되지 않는다.
파일런·켜진 파일런·작업대 세 프리팹 모두 이 값으로 맞췄다.

> 게임 수치를 정할 때 **참조할 바닐라 프리팹이 있으면 그 숫자를 그대로 쓴다.** 10과 1은 둘 다
> 그럴듯해 보였고 둘 다 틀렸다.

## 25. `SfxID`와 `SfxTableID`는 다른 것이다 (2026-09-10)

파일런 토글 효과음이 **한 번도 안 났다.** 바로 옆 줄의 `PlayPuff`는 잘 보였으므로 스크립트가 안 도는
게 아니라 **인자가 틀린** 것이었다.

```csharp
void PlaySfx(int sfxTableID, Vector3 position, ...)          // PugMod.SDK.Runtime, IAudio
API.Audio.PlaySfx(SfxTableID.acidLarvaDeath, FXPosition, …)  // SDK 예제 SpawnStuffFromTiles.cs:79
API.Audio.PlaySfx((int)SfxID.AF_portal_teleport, …)          // 우리가 하던 것
```

**`SfxTableID`는 열거형이 아니다.** `static readonly int` 필드들을 `Animator.StringToHash(이름)`으로
만든 정적 클래스라 값이 **문자열 해시**다. 반면 `SfxID`는 평범한 순차 열거형이다. 순차 번호를
캐스팅해 넣으면 **어떤 해시와도 맞지 않는다** — 호출은 성공하고 소리만 안 나며 로그에 아무것도
안 남는다.

> **조용히 틀리는 종류다.** 컴파일도 되고 예외도 없고 경고도 없다. 이름이 비슷한 타입 둘 중 하나를
> 캐스팅으로 밀어 넣을 때는 **SDK 예제가 무엇을 넘기는지** 먼저 본다.

쓸 만한 짝: `coreBossOrbPowerUp` / `coreBossOrbPowerDown`(한 장치의 기동/정지),
`AFSFXPortalAppear`(짝 없음), `switchClickGenericSfx`(수수한 대안). 이름 전체 목록은
`Pug.Base.dll`의 `SfxTableID`에 있고 1395개다.

## 26. 보이는 것은 전부 렌더 좌표에 있다 — `RenderOrigo` (2026-09-10)

렌즈 마커가 **한 달 동안 안 보였다.** 로그는 렌즈 인식 → 마커 생성 → 배치까지 다 됐다고 했고,
재질·정렬 레이어·순서·레이어·회전·색·높이·`_transparancy`까지 게임 아이콘과 하나씩 대조해 전부
같았다. 그래도 안 보였다.

답은 `PlacementIcon.LateUpdate`의 마지막 줄에 있었다.

```csharp
Vector3 vec = Manager.camera.RenderOrigo + transform.position;   // 타일 좌표 = 원점 + 렌더 좌표
```

**게임의 모든 보이는 트랜스폼은 월드 좌표가 아니라 렌더 좌표에 놓인다.**

```csharp
// CameraManager
public Vector3Int RenderOrigo { get; private set; }
Vector3Int val = moveOrigo ? m_cameraCurrentPosition.RoundToInt() : Vector3Int.zero;   // 카메라 위치

// EntityMonoBehaviour
public Vector3 WorldPosition { get; protected set; }
public Vector3 RenderPosition => WorldPosition - Manager.camera.RenderOrigo;
public static Vector3 ToRenderFromWorld(Vector3 p) => p - Manager.camera.RenderOrigo;
public static Vector3 ToWorldFromRender(Vector3 p) => p + Manager.camera.RenderOrigo;
```

카메라의 반올림 위치가 원점이고, 화면에 있는 것은 항상 0 근처에 있어서 스폰에서 아무리 멀어도 float
정밀도가 유지된다. 시뮬레이션이 주는 위치(파일런 타일, `LocalToWorld`)는 전부 월드 좌표다. 우리는
마커를 **월드 타일 좌표에 그대로** 놨으니, 플레이어의 월드 위치만큼 어긋난 자리 — 화면 밖 — 에 있었다.

### 26-1. 왜 진단이 한 달을 헤맸나

- "그려진다"는 로그는 참이었다. 그려지는 **자리**가 틀렸을 뿐이다
- 참조 아이콘과의 대조는 **외형** 항목만 했다. 좌표계는 대조 항목에 없었다 — `transform.position`
  이 당연히 월드라고 생각했기 때문이다
- 플레이어 타일도 `player.transform.position`(렌더)으로 읽고 파일런(월드)과 거리를 재고 있었다.
  기지가 스폰 근처라 원점이 작아 **거리 필터는 우연히 통과**했고, 그래서 "4개 그림" 로그가 찍혔다

### 26-2. 규칙

- **트랜스폼에 놓는 위치는 `EntityMonoBehaviour.ToRenderFromWorld`를 거친다.** 매 프레임 —
  원점이 카메라를 따라 움직인다
- **플레이어 위치를 시뮬레이션 좌표와 비교할 때는 `WorldPosition`을 읽는다**
- `EntityMonoBehaviour` 자신의 `transform.position`은 이미 렌더 좌표다. 거기에 오프셋을 더해
  `PlayPuff`에 넘기는 것은 변환이 필요 없다 (중앙 플래시가 그 증거)
- **"존재한다"와 "보인다" 사이에는 좌표계가 하나 더 있다.** 그려지는데 안 보이면 외형보다 먼저
  어느 공간에 놨는지 본다

## 7. 열린 질문 / 다음 검증

- [ ] 로컬 모드 활성화 절차 (인게임 모드 메뉴에서 자동 인식되는지, 수동 활성화 필요한지)
- [ ] 재적용에 전체 재시작이 필요한지, 부분 재로드로 되는지
- [ ] 배포(mod.io/창작마당) 절차 — 6단계에서 조사 (UploadMod.cs / SteamWorkshopTab.cs 존재 확인만 됨)
- [x] ~~그래픽 프리팹의 나머지 참조~~ — SpriteAsset은 `m_address`(=파일 guid)로 연결된다.
  GradientMap은 스킨용 선택 기능이라 안 쓴다 (11장)
- [~] 스프라이트 오프셋 규칙 — 업라이트 스프라이트의 `localPosition` y·z를 텍스처 크기에서
  어떻게 잡는지. SDK 작업대 값 `(0, 0.0625, -0.3125)`를 그대로 쓰는데, **아트를 그 예제와 같은
  16×18로 맞추면서 같은 조건이 됐다**(19장·11장). 그래도 어긋나 보이면 여기다
- [ ] **`ImmunityZoneCD`를 쓸 것인가** (21-5) — 삽·호미·폭발을 근원에서 한 번에 막는 게임 자체
  장치지만 `immune` 타일이 세이브에 남는다. 모드를 지우면 그 땅이 영구히 안 부서진다. **결정 필요**
- [ ] 인벤토리 아이콘이 실제로 그려지는가 — 번들이 Sprite를 안 넘겨준다는 사실(19-3)이
  `icon`(`fileID: 21300000`)에도 해당되는지. 번들 내부 참조라 다를 수 있어 미확정
