# 게임 오브젝트 데이터 (자동 추출)

게임 전체 오브젝트 DB를 한 번에 뜯어 만든 정적 참조표. **추측·반복테스트 대신 이 표를 보고 설계한다.**

- `object_flags.csv` — 유니크 ObjectID **2279개** × 컴포넌트 유무 플래그
- 추출: `NoBreakZoneDatabaseDumpSystem`이 로드 시 `PugDatabase.objectInfos` 전체를 순회하며 각 프리팹에 `HasComponent<T>`를 찍어 `Player.log`에 덤프 → CSV로 정리
- 컴포넌트 **이름**은 릴리즈 빌드에서 못 읽음(DebugTypeName 스트립 + 모드에서 리플렉션 금지). 그래서 **이름이 아니라 "관심 컴포넌트 유무"** 를 찍는다.

- `script_fileids.csv` — 게임 클래스 **3235개** × 유니티 `m_Script` fileID (아래 참조)
- `script_guids.csv` — 프로젝트의 모든 MonoScript **8186개** × `(fileID, guid)` (아래 참조).
  **프리팹을 쓸 때 참조할 표는 이쪽이다.**

## 컬럼

| 컬럼 | 컴포넌트 | 의미 |
| --- | --- | --- |
| id | — | ObjectID |
| type | (EntityObjectInfo.objectType) | ObjectType (PlaceablePrefab=설치물 등) |
| tileType | (EntityObjectInfo.tileType) | 타일 종류 (none/wall/ore/floor…) |
| health | HealthCD | 체력 있음(파괴 파이프라인 대상) |
| damageable | DamageableObjectCD | |
| destructible | DestructibleObjectCD | **월드 파괴물**(광석·항아리·배럴·고대파괴물) 표식 |
| dontDestroy | DontDestroyOnZeroHealthCD | 0체력에도 파괴 안 됨(기본 보유 여부) |
| immune | ImmuneToDamageCD | |
| lootTable | DropsLootFromLootTableCD | **전리품 테이블 드롭**(항아리·고대파괴물·벽) |
| lootOnDmg | DropsLootWhenDamagedCD | **피격 시 전리품 드롭**(광석 덩어리) — 자원복제 핵심 |
| dontDropSelf | DontDropSelfCD | 자기 자신을 안 떨굼 (745/747이 보유 → 판별력 없음) |
| dontDropLoot | DontDropLootCD | |
| mineable | MineableCD | 곡괭이 대상. **설치물 대부분 보유**(회수용) → 판별력 없음 |
| diggable | DiggableCD | 삽 대상. 전선 등 설치물도 보유 → 판별력 없음 |
| requiresDrill | RequiresDrillCD | 드릴 필요(광석) |
| tileCD | TileCD | 타일 엔티티(벽·바닥·지형) |
| plant | PlantCD | |
| growing | GrowingCD | 성장 중(씨앗/작물). 이들은 **health=0** |
| critter | CritterCD | |
| catTags | ObjectCategoryTagsCD | 카테고리 태그 보유 |

## 확정된 보호 판별 규칙 (No Break Zone)

전체 2279개로 검증한 결과 (`기획서 §6` "부수면 자기 자신이 돌아오는가"와 일치):

> ⚠️ 아래 규칙은 **벽·바닥을 보호 대상에 넣기 전**의 것이다. 지금 규칙은
> `Scripts/Logic/NoBreakZoneProtectionRule.cs`가 원본이고, **692개**(설치물 569 + 타일 123)를
> 보호한다. `Editor/LogicTests~`가 그 숫자를 강제한다.

**보호(PROTECT)** = `type==PlaceablePrefab` **AND** `health==1` **AND** `tileCD==0` **AND NOT(`destructible` OR `lootTable` OR `lootOnDmg`)**

→ 569개 보호. 상자·조명·가구·받침대·보스상자 등 설치물 전부 포함.

**제외(SKIP)** = `destructible` 또는 `lootTable` 또는 `lootOnDmg` 중 하나라도 보유, 또는 TileCD.
→ 모든 `*OreBoulder`·`*Destructible`·항아리·크리스탈·`Wall*Block`(벽) 제외. **광석 누출 0 = 자원복제 안전.**

### 검증으로 확인된 사실
- `MineableCD`·`DiggableCD`·`DontDropSelfCD`는 설치물도 대부분 보유 → **판별자로 못 씀** (초기 시행착오. `mineable` 기준으로 짰다가 상자가 전부 제외됐었음).
- 씨앗·작물은 `health=0`이라 애초에 보호 쿼리(health 필요)에 안 걸림 → 수확 안전.
- 나무·덤불·풀·트로피 ~123개는 과보호되지만 자원복제 아님(4단계 검증엔 무해). 파일런 범위 도입 후 재검토.

## 재생성 방법
`NoBreakZoneDatabaseDumpSystem`의 `RunAudit`을 켜고 빌드→설치→게임 1회 로드→월드 진입.
`Player.log`의 `[NBZDB]` 라인을 뽑아 CSV로 저장. 게임 업데이트로 오브젝트가 바뀌면 다시 뜬다.

⚠️ **`[NBZDB]` 줄을 통째로 뽑으면 안 된다.** 덤프가 찍는 `BEGIN objectInfos=…`·`END unique=…`
마커와 `COLUMNS=` 헤더까지 딸려 들어온다. 실제로 그렇게 만들어져서 마커 2줄이 데이터 한가운데
박혔고(추출 후 알파벳 정렬을 해서), 당시의 전수 회귀 테스트가
`KeyNotFoundException: 'type'`이라는 엉뚱한 얼굴로 실패했다.

빼야 할 것: `BEGIN`/`END` 마커, `COLUMNS=` 헤더, 그리고 프리팹이 없어 4칸만 찍히는
`None,NonUsable,none,<no-prefab>` 행. **넷을 다 빼면 2278행**이 남고 모든 행이 20칸을 채운다.
(`END unique=2279`는 프리팹 없는 `None`까지 센 숫자다.)

**이 정리는 실제로 두 번 빠뜨렸다.** 2026-09-05 시점의 CSV에 넷이 그대로 들어 있었고,
`WholeDatabaseRegression`이 `BEGIN` 행에서 `row["type"]`을 찾다 `KeyNotFoundException`으로
죽고 있었다 — 규칙이 깨진 것처럼 보이지만 아니다. 재생성했으면 **행 수를 세서 2278인지 확인한다.**

---

# script_fileids.csv — 프리팹 `m_Script` 역참조표

프리팹 YAML은 컴포넌트를 `m_Script: {fileID: N, guid: G}` 로 가리킨다. 이 표가 있으면
**유니티 없이 프리팹을 작성하고 검증할 수 있다.** `Editor/preflight.py`가 이걸 읽는다.

| 컬럼 | 의미 |
| --- | --- |
| fileID | 유니티가 그 클래스에 부여하는 값 |
| assembly | 어느 게임 어셈블리 소속인지 (`Pug.ECS.Authoring` 이 프리팹용) |
| fullName | 네임스페이스 포함 클래스명 |

**fileID는 클래스 이름에서 결정론적으로 계산된다:**

```
fileID = int32_le( MD4(b"s\0\0\0" + 네임스페이스 + 클래스명)[:4] )
```

게임 authoring 어셈블리 guid는 **`3392f4c23e1d8662d749dabb2361ee02`** 고정이다.

검증: SDK 예제와 레퍼런스 모드의 프리팹 20개에서 뽑은 게임 어셈블리 참조 **184건이 100% 일치**했다.
표 안에서 fileID 충돌은 0건.

## 재생성 방법
게임 디컴파일 소스(`Adrriiannn/ck-db`)를 클론한 뒤 클래스마다 위 식을 적용해 CSV로 쓴다.
`Editor/preflight.py`의 `md4`·`script_file_id` 함수가 같은 구현이므로 그걸 가져다 쓰면 된다.
게임 업데이트로 클래스가 추가·개명되면 다시 뜬다.

---

# script_guids.csv — 유니티가 답한 `m_Script` 정답표

프리팹의 `m_Script: {fileID, guid}`에 무엇을 써야 하는지에 대한 **유일한 출처**다.

| 컬럼 | 의미 |
| --- | --- |
| fullName | 네임스페이스 포함 클래스명 |
| fileID | 유니티가 그 스크립트에 부여하는 값 |
| guid | 그 스크립트가 사는 에셋(`.dll` 또는 `.cs`)의 guid |
| assembly | 소속 어셈블리 이름 |
| path | 그 에셋의 프로젝트 경로 |

**레퍼런스 모드나 `Assets/Examples`에서 guid를 베끼지 않는다.** 그렇게 해서 모든 참조가
끊긴 채로 빌드가 성공한 사고가 있었다 — `research.md` 11장에 전말이 있다.

`Editor/genassets.py`의 `game_script()`가 이 표를 조회하고, `Editor/preflight.py`가 생성된
프리팹·`.asset`의 모든 참조를 이 표와 대조한다.

## 재생성 방법

```
Unity.exe -batchmode -quit -projectPath <유니티 프로젝트> ^
  -executeMethod NoBreakZone.EditorTools.DumpScriptGuids.Dump
```

`<유니티 프로젝트>`의 실제 값은 `Editor/build.ps1`의 `-ProjectPath` 기본값이다.

게임·SDK 업데이트로 어셈블리가 바뀌면 다시 뜨고, `genassets.py`를 재실행한다.
