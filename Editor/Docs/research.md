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

## 7. 열린 질문 / 다음 검증

- [ ] 로컬 모드 활성화 절차 (인게임 모드 메뉴에서 자동 인식되는지, 수동 활성화 필요한지)
- [ ] 재적용에 전체 재시작이 필요한지, 부분 재로드로 되는지
- [ ] 배포(mod.io/창작마당) 절차 — 6단계에서 조사 (UploadMod.cs / SteamWorkshopTab.cs 존재 확인만 됨)
