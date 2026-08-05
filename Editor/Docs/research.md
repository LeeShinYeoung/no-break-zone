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

### guid 는 어셈블리를 가리킨다 — 프리팹에서 베낀다

| guid | 무엇 | 확인 방법 |
| --- | --- | --- |
| `3392f4c23e1d8662d749dabb2361ee02` | 게임 authoring 어셈블리 (`ObjectAuthoring`·`HealthAuthoring`·`PlaceableObjectAuthoring` 등) | 레퍼런스 프리팹 184회 등장 |
| `6f4e9f12d8be4d048a7b574866c31a4f` + fileID `11500000` | `EntityMonoBehaviour` (그래픽 프리팹 루트) | 필드(`XScaler`·`spriteObjects`·`interactable`)로 `Pug.Other/EntityMonoBehaviour.cs` 확인 |
| `292700ef68995bdb2163e35989fc7eb0` + fileID `1908045241` | SpriteObject (그래픽 자식) | `spriteObjects` 배열 원소 |

⚠️ **SDK의 `Assets/ModSDK/Data/MetaFiles.zip`(`.dll.meta` 114개)의 guid는 프리팹이 쓰는 것과
다르다.** 대조해 봤으나 한 건도 안 겹친다. **프리팹에서 관측한 guid를 쓸 것.**

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
렌더된다"는 성립하지 않는다. 다만 *조치*(32로 둔다)는 32px 텍스처에 대해 여전히 맞다 — 아이콘
쪽에는 그게 정확한 값이다. 바뀌는 것은 **기대하는 결과**다. 파일런은 1×1 타일을 차지하면서
2×2 타일 크기로 보인다.

초안을 16px로 줄이는 선택지의 비용도 다시 재봤다. 32px 초안은 16px 그림을 2배 확대한 것이
**아니다** — 2×2 픽셀 블록이 단색인 비율이 73%뿐이라 나머지 27%는 진짜 서브픽셀 디테일이다.
색 수가 11개뿐이라 축소해도 형태는 읽히지만 무손실은 아니다.

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
두 파일런의 이음매에 걸친 2×1 작업대는 보호된다. 파일런이 하나면 "한 파일런 기준"과 결과가 같다.

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

## 7. 열린 질문 / 다음 검증

- [ ] 로컬 모드 활성화 절차 (인게임 모드 메뉴에서 자동 인식되는지, 수동 활성화 필요한지)
- [ ] 재적용에 전체 재시작이 필요한지, 부분 재로드로 되는지
- [ ] 배포(mod.io/창작마당) 절차 — 6단계에서 조사 (UploadMod.cs / SteamWorkshopTab.cs 존재 확인만 됨)
- [x] ~~그래픽 프리팹의 나머지 참조~~ — SpriteAsset은 `m_address`(=파일 guid)로 연결된다.
  GradientMap은 스킨용 선택 기능이라 안 쓴다 (11장)
- [ ] 스프라이트 오프셋 규칙 — 업라이트 스프라이트의 `localPosition` y·z를 텍스처 크기에서
  어떻게 잡는지. SDK 작업대 값 `(0, 0.0625, -0.3125)`를 그대로 쓰고 있다. 인게임에서 정렬이
  어긋나 보이면 여기다
